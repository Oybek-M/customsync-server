using CustomSync.Api.Endpoints;
using CustomSync.Data;
using CustomSync.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// snake_case: plan 01b sync hot-path'ni raw NpgsqlCommand bilan yozadi,
// EF'ning standart PascalCase ustunlari esa har bir raw so'rovda
// qo'shtirnoq talab qilardi ("PeerHash"). Bitta unutilgan qo'shtirnoq —
// runtime xato.
builder.Services.AddDbContext<SyncDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<SettingsService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SettingsService>()
        .EnsureDefaultsAsync();
}

app.MapHealthEndpoints();

app.Run();

// Integration testlar uchun (WebApplicationFactory<Program>).
public partial class Program { }
