using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

public class SeqMonotonicityTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public SeqMonotonicityTests(DatabaseFixture fixture) => _fixture = fixture;

    private static SyncRecord Make(int index, string peerHash) => new()
    {
        RecordId    = RecordId.Compute(RecordKind.Deleted, "acc01", peerHash, index, 1753800000),
        Kind        = RecordKind.Deleted,
        AccountHash = "acc01",
        PeerHash    = peerHash,
        MsgId       = index,
        OccurredAt  = 1753800000,
        ObservedAt  = 1753800000 + index,
        DeviceId    = "test-device",
        Nonce       = new byte[12],
        Payload     = [1, 2, 3]
    };

    /// <summary>
    /// Regressiya testi: BIGSERIAL ishlatilsa bu test yiqiladi.
    /// Parallel push'lar davomida uzluksiz pull qilamiz va OXIRIDA
    /// hech bir yozuv o'tkazib yuborilmaganini tekshiramiz.
    /// </summary>
    [Fact]
    public async Task Concurrent_pushes_are_never_skipped_by_a_polling_reader()
    {
        const int total = 60;
        var peerHash = $"peer_conc_{Guid.NewGuid():N}";
        var seen = new HashSet<string>();
        var cursor = 0L;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var db = _fixture.CreateContext();
                var page = await new SyncService(db).PullAsync(cursor, 500);
                foreach (var r in page.Records.Where(r => r.PeerHash == peerHash))
                    seen.Add(r.RecordId);
                if (page.Records.Count > 0) cursor = page.NextSince;
                await Task.Delay(5);
            }
        });

        var writers = Enumerable.Range(0, total).Select(i => Task.Run(async () =>
        {
            await using var db = _fixture.CreateContext();
            await new SyncService(db).PushAsync("test-device", [Make(i, peerHash)]);
        }));
        await Task.WhenAll(writers);

        // Yozuvchilar tugagach reader oxirgi bo'lakni ham olishi uchun kutamiz.
        await Task.Delay(300);
        stop.Cancel();
        await reader;

        await using var final = _fixture.CreateContext();
        var tail = await new SyncService(final).PullAsync(cursor, 500);
        foreach (var r in tail.Records.Where(r => r.PeerHash == peerHash))
            seen.Add(r.RecordId);

        Assert.Equal(total, seen.Count);
    }

    [Fact]
    public async Task Seq_values_are_unique_and_increasing()
    {
        var peerHash = $"peer_seq_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var service = new SyncService(db);

        await service.PushAsync("test-device",
            Enumerable.Range(100, 20).Select(i => Make(i, peerHash)).ToList());

        var page = await service.PullAsync(0, 500);
        var seqs = page.Records
            .Where(r => r.PeerHash == peerHash)
            .Select(r => r.Seq)
            .ToList();

        Assert.Equal(20, seqs.Count);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.Equal(seqs.OrderBy(s => s), seqs);
    }
}
