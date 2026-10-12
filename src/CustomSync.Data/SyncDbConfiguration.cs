using Microsoft.EntityFrameworkCore;

namespace CustomSync.Data;

/// <summary>
/// `SyncDbContext` ni sozlaydigan yagona joy: production (API start-up), test
/// bazasi fixture'i va pending-model-changes testi shu yerdan o'tadi — testlar
/// tekshirgan model production ishlatadigan model bilan bir xil bo'ladi.
/// </summary>
public static class SyncDbConfiguration
{
    public static DbContextOptionsBuilder ConfigureSyncDb(
        this DbContextOptionsBuilder builder,
        string? connectionString)
    {
        // Ulanish satri yo'q bo'lsa start-up to'xtaydi. Kodga yozilgan default
        // bilan davom etish noto'g'ri bazaga ulanishni yoki chalg'ituvchi
        // autentifikatsiya xatosini berardi.
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "The PostgreSQL connection string (ConnectionStrings:Postgres) is not configured.");

        return builder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
    }

    public static DbContextOptionsBuilder<SyncDbContext> ConfigureSyncDb(
        this DbContextOptionsBuilder<SyncDbContext> builder,
        string? connectionString)
    {
        // Ikkinchi nusxa yo'q: generic overload yuqoridagisini chaqiradi.
        ((DbContextOptionsBuilder)builder).ConfigureSyncDb(connectionString);
        return builder;
    }
}
