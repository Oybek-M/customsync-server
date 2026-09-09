using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CustomSync.Tests;

public class StorageMetricsTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private static long _seq = 0;

    public StorageMetricsTests(DatabaseFixture fixture) => _fixture = fixture;

    private static async Task ClearTablesAsync(SyncDbContext db)
    {
        await db.Records.ExecuteDeleteAsync();
        await db.MediaBlobs.ExecuteDeleteAsync();
    }

    private static RecordEntity MakeRecord(int size, DateTime receivedAt) => new()
    {
        RecordId    = Guid.NewGuid().ToString("N"),
        Seq         = Interlocked.Increment(ref _seq),
        Kind        = "activity",
        AccountHash = "acc01",
        PeerHash    = "peer01",
        MsgId       = 1,
        OccurredAt  = 1000,
        ObservedAt  = 1000,
        DeviceId    = "test-dev",
        Nonce       = new byte[12],
        Payload     = new byte[size],
        PayloadSize = size,
        ReceivedAt  = receivedAt
    };

    private static MediaBlobEntity MakeMedia(long size, DateTime uploadedAt) => new()
    {
        Hash        = Guid.NewGuid().ToString("N"),
        Size        = size,
        Nonce       = new byte[12],
        StoragePath = "/tmp/dummy",
        UploadedAt  = uploadedAt
    };

    [Fact]
    public async Task Empty_database_reports_zero_without_dividing_by_zero()
    {
        await using var db = _fixture.CreateContext();
        await ClearTablesAsync(db);
        var stats = new StatsService(db);

        var summary = await stats.SummaryAsync();

        Assert.Equal(0, summary.TotalBytes);
        Assert.Equal(0, summary.RecordCount);
        Assert.Equal(0, summary.RecordBytes);
        Assert.Equal(0, summary.MediaCount);
        Assert.Equal(0, summary.MediaBytes);
        Assert.Equal(0, summary.DailyGrowthBytes);
        Assert.Null(summary.DaysUntilFull);
    }

    [Fact]
    public async Task Zero_growth_with_capacity_reports_null_days_until_full()
    {
        await using var db = _fixture.CreateContext();
        await ClearTablesAsync(db);
        var stats = new StatsService(db);

        // O'sish nol bo'lganda (baza bo'sh yoki o'sish yo'q), server to'lib ketmaydi,
        // shuning uchun DaysUntilFull 0 emas, null bo'lishi shart.
        var summary = await stats.SummaryAsync(diskCapacityBytes: 1_000_000);

        Assert.Equal(0, summary.DailyGrowthBytes);
        Assert.Null(summary.DaysUntilFull);
    }

    [Fact]
    public async Task Server_younger_than_window_reflects_observed_days_not_fixed_seven()
    {
        await using var db = _fixture.CreateContext();
        await ClearTablesAsync(db);
        var stats = new StatsService(db);

        // 2 kunlik ma'lumot: umumiy 14,000 bayt.
        // Agar qat'iy 7 ga bo'linsa: 14,000 / 7 = 2,000 bo'lib qolardi.
        // Haqiqiy kuzatilgan oyna (2 kun) bo'yicha: 14,000 / 2 = 7,000 bo'lishi kerak.
        var now = DateTime.UtcNow;
        db.Records.Add(MakeRecord(6_000, now.AddDays(-2)));
        db.Records.Add(MakeRecord(8_000, now.AddDays(-1)));
        await db.SaveChangesAsync();

        var summary = await stats.SummaryAsync();

        Assert.Equal(14_000, summary.RecordBytes);
        Assert.Equal(14_000, summary.TotalBytes);
        Assert.Equal(7_000, summary.DailyGrowthBytes);
        Assert.NotEqual(2_000, summary.DailyGrowthBytes);
    }

    [Fact]
    public async Task Media_inside_window_contributes_to_daily_growth()
    {
        await using var db = _fixture.CreateContext();
        await ClearTablesAsync(db);
        var stats = new StatsService(db);

        // 2 kunlik oyna: yozuvlar 2,000 bayt, media 8,000 bayt -> jami 10,000 bayt.
        // Faqat yozuvlar bo'yicha: 2,000 / 2 = 1,000 bo'lardi.
        // Media bilan birga: 10,000 / 2 = 5,000 bo'lishi shart.
        var now = DateTime.UtcNow;
        db.Records.Add(MakeRecord(2_000, now.AddDays(-2)));
        db.MediaBlobs.Add(MakeMedia(8_000, now.AddDays(-1)));
        await db.SaveChangesAsync();

        var summary = await stats.SummaryAsync();

        Assert.Equal(10_000, summary.TotalBytes);
        Assert.Equal(2_000, summary.RecordBytes);
        Assert.Equal(8_000, summary.MediaBytes);
        Assert.Equal(5_000, summary.DailyGrowthBytes);
        Assert.NotEqual(1_000, summary.DailyGrowthBytes);
    }

    [Fact]
    public async Task Disk_over_capacity_reports_zero_days_until_full()
    {
        await using var db = _fixture.CreateContext();
        await ClearTablesAsync(db);
        var stats = new StatsService(db);

        // Disk sig'imi 5,000 bayt, mavjud ma'lumot 10,000 bayt (allaqachon to'lgan).
        // DaysUntilFull manfiy emas, aynan 0 bo'lishi shart.
        var now = DateTime.UtcNow;
        db.Records.Add(MakeRecord(10_000, now.AddDays(-1)));
        await db.SaveChangesAsync();

        var summary = await stats.SummaryAsync(diskCapacityBytes: 5_000);

        Assert.Equal(10_000, summary.TotalBytes);
        Assert.True(summary.DailyGrowthBytes > 0);
        Assert.Equal(0, summary.DaysUntilFull);
    }
}
