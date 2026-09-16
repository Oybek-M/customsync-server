using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using CustomSync.Api.Auth;
using CustomSync.Api.Endpoints;
using CustomSync.Api.Realtime;
using CustomSync.Data;
using CustomSync.Services;
using CustomSync.Services.Storage;
using CustomSync.Services.Storage.Targets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// Serilog'ni DI konteyner va baza mavjud bo'lishidan OLDIN,
// eng birinchi qadamda ishga tushiramiz. Bu "bootstrap" bosqich:
// connection string kabi fayl loglarining saqlash muddati ham
// (retainedFileCountLimit, rollingInterval) dastur ishga tushganda
// kerak bo'ladi — shuning uchun ular server_settings'da emas,
// appsettings.json da turadi (xuddi connection string singari).
var tmpConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .Build();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        tmpConfig["Serilog:File:Path"] ?? "logs/customsync-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: int.TryParse(
            tmpConfig["Serilog:File:RetainedFileCountLimit"], out var n) ? n : 14)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// snake_case: plan 01b sync hot-path'ni raw NpgsqlCommand bilan yozadi,
// EF'ning standart PascalCase ustunlari esa har bir raw so'rovda
// qo'shtirnoq talab qilardi ("PeerHash"). Bitta unutilgan qo'shtirnoq —
// runtime xato.
builder.Services.AddDbContext<SyncDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());
// HTTP JSON ham snake_case -- spec, .cmx almashuv formati va tdesktop
// agenti (plan 02) hammasi shu shaklni ishlatadi. Standart camelCase'da
// qolsa, klient yuborgan "record_id" serverga null bo'lib yetib borardi
// va xato faqat runtime'da, tushunarsiz validatsiya xabari sifatida
// ko'rinardi.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<JwtIssuer>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<KeyWrapService>();
builder.Services.AddScoped<RecordQueryService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<InterchangeService>();
builder.Services.AddScoped<PurgeService>();
var mediaRoot = builder.Configuration["Storage:MediaRoot"] ?? "/var/lib/customsync/media";
var archiveStagingRoot = builder.Configuration["Storage:ArchiveStagingRoot"] ?? "/var/lib/customsync/staging";
ManualDownloadTarget.ValidateRoots(archiveStagingRoot, mediaRoot);

builder.Services.AddScoped(sp => new MediaService(
    sp.GetRequiredService<SyncDbContext>(),
    mediaRoot));
builder.Services.AddScoped(sp => new ManualDownloadTarget(archiveStagingRoot));
builder.Services.AddScoped<IArchiveTarget>(sp => sp.GetRequiredService<ManualDownloadTarget>());
builder.Services.AddScoped(sp => new CustomSync.Services.Releases.UploadSessionStore(
    sp.GetRequiredService<SyncDbContext>(),
    builder.Configuration["Storage:ReleasesRoot"] ?? "/var/lib/customsync/releases"));
builder.Services.AddScoped(sp => new CustomSync.Services.Releases.ReleaseService(
    sp.GetRequiredService<SyncDbContext>(),
    sp.GetRequiredService<CustomSync.Services.Releases.UploadSessionStore>(),
    sp.GetRequiredService<SettingsService>(),
    builder.Configuration["Storage:ReleasesRoot"] ?? "/var/lib/customsync/releases"));
builder.Services.AddSingleton<DeviceRevocationCache>();
builder.Services.AddSingleton<CustomSync.Api.Realtime.NotifyHub>();

builder.Services.AddSingleton<IDiskProbe, SystemDiskProbe>();
builder.Services.AddScoped<ArchiveJobRunner>();
builder.Services.AddHostedService<ArchiveJobService>();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("keywrap", context =>
    {
        var deviceId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        var settings = context.RequestServices.GetRequiredService<SettingsService>();
        // Sozlama qiymati partitsiya birinchi marta yaratilganda olinadi.
        // Agar sozlama keyinroq o'zgartirilsa, yangi partitsiyalarga ta'sir qiladi.
        // Har so'rovda rate limiterni qayta qurish cheklovni butunlay o'chirib
        // qo'yishi sababli bu maqbul yechim.
        var limit = settings.GetIntAsync("auth.wrap_rate_per_hour").GetAwaiter().GetResult();

        return RateLimitPartition.GetFixedWindowLimiter(
            deviceId,
            _ => new FixedWindowRateLimiterOptions
            {
                // 0 bu yerda CHEKSIZ degani emas. `storage.quota_*` va
                // `retention.*_days` da 0 = cheksiz, lekin bu brute-force
                // himoyasi: noto'g'ri qiymat himoyani jimgina o'chirib
                // qo'ymasligi uchun xavfsiz standartga qaytadi.
                PermitLimit = limit > 0 ? limit : 5,
                Window      = TimeSpan.FromHours(1),
                QueueLimit  = 0
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});



var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey sozlanmagan yoki 32 belgidan qisqa. " +
        "appsettings.Development.json ga tasodifiy kalit yozing.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer      = builder.Configuration["Jwt:Issuer"],
            ValidAudience    = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"]!)),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var cache = ctx.HttpContext.RequestServices
                    .GetRequiredService<DeviceRevocationCache>();
                var deviceId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

                if (deviceId is null || cache.IsRevoked(deviceId))
                    ctx.Fail("device_revoked");

                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/ws"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(o =>
    o.AddPolicy("admin", p => p.RequireRole("admin")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SettingsService>()
        .EnsureDefaultsAsync();

    // Bekor qilingan qurilmalarni in-memory keshga yuklash
    var revokedCache = scope.ServiceProvider.GetRequiredService<DeviceRevocationCache>();
    var revokedIds = await db.Devices
        .Where(d => d.RevokedAt != null)
        .Select(d => d.DeviceId)
        .ToListAsync();
    revokedCache.Load(revokedIds);
}

if (args.Contains("--create-enrollment-code"))
{
    using var bootstrapScope = app.Services.CreateScope();
    var devices = bootstrapScope.ServiceProvider.GetRequiredService<DeviceService>();
    var role = args.Contains("--admin") ? "admin" : "device";
    Console.WriteLine($"Enrollment code: {await devices.CreateEnrollmentCodeAsync(role)}");
    return;
}

app.UseWebSockets();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapDeviceEndpoints();
app.MapSettingsEndpoints();
app.MapSyncEndpoints();
app.MapMediaEndpoints();
app.MapKeyEndpoints();
app.MapRecordEndpoints();
app.MapStatsEndpoints();
app.MapInterchangeEndpoints();
app.MapReleaseEndpoints();

app.Map("/ws/notify", async (HttpContext context, NotifyHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var deviceId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (deviceId is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    hub.Register(deviceId, socket);

    // Klient hech narsa yubormaydi; ulanish yopilguncha ushlab turamiz.
    var buffer = new byte[256];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                }
                break;
            }
        }
    }
    catch (WebSocketException) { /* uzilish — normal holat */ }

    hub.Prune();
}).RequireAuthorization();

app.Run();




// Integration testlar uchun (WebApplicationFactory<Program>).
public partial class Program { }
