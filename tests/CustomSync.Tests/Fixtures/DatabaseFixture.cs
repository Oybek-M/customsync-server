using System.Text.Json;
using System.Text.RegularExpressions;
using CustomSync.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CustomSync.Tests.Fixtures;

public class DatabaseFixture : IAsyncLifetime
{
    // `appsettings.Development.json` gitignore'da -- parol shu yerdan
    // o'qiladi, kodga yozilmaydi (handoff §4.1).
    private static readonly string AdminConnection = ResolveAdminConnection();

    private static string ResolveAdminConnection()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CUSTOMSYNC_TEST_DB");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CustomSync.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                "CustomSync.sln topilmadi -- test qayerdan ishga tushganini aniqlab bo'lmadi.");

        var settingsPath = Path.Combine(
            dir.FullName, "src", "CustomSync.Api", "appsettings.Development.json");

        if (!File.Exists(settingsPath))
            throw new InvalidOperationException(
                $"{settingsPath} yo'q. Avval scripts\\db-bootstrap.ps1 ni ishga tushiring.");

        using var stream = File.OpenRead(settingsPath);
        var connection = JsonDocument.Parse(stream)
            .RootElement.GetProperty("ConnectionStrings")
            .GetProperty("Postgres").GetString();

        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:Postgres bo'sh.");

        // Fixture o'z test bazasini YARATADI, shuning uchun avval `postgres`
        // bazasiga ulanadi.
        return Regex.Replace(connection, @"Database=[^;]*", "Database=postgres");
    }

    public string DatabaseName { get; } = $"customsync_test_{Guid.NewGuid():N}";
    public string ConnectionString => AdminConnection.Replace("Database=postgres", $"Database={DatabaseName}");

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnection))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public SyncDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SyncDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new SyncDbContext(options);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}
