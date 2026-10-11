using Microsoft.EntityFrameworkCore;

namespace CustomSync.Data;

public static class SyncDbConfiguration
{
    public const string DefaultConnectionString =
        "Host=localhost;Database=customsync;Username=customsync;Password=dummy";

    public static DbContextOptionsBuilder ConfigureSyncDb(
        this DbContextOptionsBuilder builder,
        string? connectionString = null)
    {
        return builder
            .UseNpgsql(string.IsNullOrWhiteSpace(connectionString) ? DefaultConnectionString : connectionString)
            .UseSnakeCaseNamingConvention();
    }

    public static DbContextOptionsBuilder<SyncDbContext> ConfigureSyncDb(
        this DbContextOptionsBuilder<SyncDbContext> builder,
        string? connectionString = null)
    {
        return builder
            .UseNpgsql(string.IsNullOrWhiteSpace(connectionString) ? DefaultConnectionString : connectionString)
            .UseSnakeCaseNamingConvention();
    }
}
