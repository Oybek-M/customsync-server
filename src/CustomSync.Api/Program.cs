using System.Text;
using CustomSync.Api.Auth;
using CustomSync.Api.Endpoints;
using CustomSync.Data;
using CustomSync.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// snake_case: plan 01b sync hot-path'ni raw NpgsqlCommand bilan yozadi,
// EF'ning standart PascalCase ustunlari esa har bir raw so'rovda
// qo'shtirnoq talab qilardi ("PeerHash"). Bitta unutilgan qo'shtirnoq —
// runtime xato.
builder.Services.AddDbContext<SyncDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<JwtIssuer>();

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
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SettingsService>()
        .EnsureDefaultsAsync();
}

if (args.Contains("--create-enrollment-code"))
{
    using var bootstrapScope = app.Services.CreateScope();
    var devices = bootstrapScope.ServiceProvider.GetRequiredService<DeviceService>();
    Console.WriteLine($"Enrollment code: {await devices.CreateEnrollmentCodeAsync()}");
    return;
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapDeviceEndpoints();
app.MapSettingsEndpoints();

app.Run();

// Integration testlar uchun (WebApplicationFactory<Program>).
public partial class Program { }
