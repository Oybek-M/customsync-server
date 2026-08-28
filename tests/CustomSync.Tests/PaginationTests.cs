using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

public class PaginationTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public PaginationTests(DatabaseFixture fixture) => _fixture = fixture;

    private static SyncRecord Make(string peerHash, int index, long occurredAt, string kind = RecordKind.Deleted) => new()
    {
        RecordId    = RecordId.Compute(kind, "acc01", peerHash, index, occurredAt),
        Kind        = kind,
        AccountHash = "acc01",
        PeerHash    = peerHash,
        MsgId       = index,
        OccurredAt  = occurredAt,
        ObservedAt  = occurredAt,
        DeviceId    = "test-device",
        Nonce       = new byte[12],
        Payload     = [1]
    };

    [Fact]
    public async Task Paging_under_concurrent_inserts_yields_no_duplicates_and_no_gaps()
    {
        var peerHash = $"peer_p_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var sync  = new SyncService(db);
        var query = new RecordQueryService(db);

        await sync.PushAsync("test-device",
            Enumerable.Range(0, 40).Select(i => Make(peerHash, i, 1_700_000_000 + i)).ToList());

        var snapshot = await query.CurrentSnapshotAsync();
        var seen = new List<string>();
        long? afterKey = null;
        long? afterSeq = null;

        while (true)
        {
            var page = await query.QueryAsync(new RecordQuery
            {
                Snapshot   = snapshot,
                Limit      = 10,
                Descending = true,
                AfterKey   = afterKey,
                AfterSeq   = afterSeq,
                PeerHash   = peerHash
            });
            if (page.Count == 0) break;

            seen.AddRange(page.Select(r => r.RecordId));
            afterKey = page[^1].OccurredAt;
            afterSeq = page[^1].Seq;

            // Sahifalar orasida yangi ma'lumot keladi — snapshot uni
            // ushbu sahifalashdan tashqarida ushlab turishi kerak.
            await sync.PushAsync("test-device",
                [Make(peerHash, 1000 + seen.Count, 1_800_000_000 + seen.Count)]);
        }

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }

    [Fact]
    public async Task Ascending_and_descending_return_exact_reverse_order()
    {
        var peerHash = $"peer_rev_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var sync  = new SyncService(db);
        var query = new RecordQueryService(db);

        await sync.PushAsync("test-device",
            Enumerable.Range(0, 15).Select(i => Make(peerHash, i, 1_600_000_000 + i)).ToList());
        var snapshot = await query.CurrentSnapshotAsync();

        var desc = await query.QueryAsync(new RecordQuery
        {
            Snapshot   = snapshot,
            Limit      = 100,
            Descending = true,
            PeerHash   = peerHash
        });
        var asc = await query.QueryAsync(new RecordQuery
        {
            Snapshot   = snapshot,
            Limit      = 100,
            Descending = false,
            PeerHash   = peerHash
        });

        Assert.Equal(desc.Select(r => r.RecordId),
                     asc.Select(r => r.RecordId).Reverse());
    }

    [Fact]
    public async Task Paging_with_identical_occurred_at_uses_seq_tiebreaker()
    {
        // 20 ta yozuv barchasi AYNAN BIR XIL occurred_at oladi.
        // Sahifa hajmi 5. seq tiebreaker bo'lmasa qatorlar takrorlanadi yoki tushib qoladi.
        var peerHash = $"peer_tie_{Guid.NewGuid():N}";
        const long sameOccurredAt = 1_750_000_000L;
        const int totalRecords = 20;

        await using var db = _fixture.CreateContext();
        var sync  = new SyncService(db);
        var query = new RecordQueryService(db);

        await sync.PushAsync("test-device",
            Enumerable.Range(0, totalRecords).Select(i => Make(peerHash, i, sameOccurredAt)).ToList());

        var snapshot = await query.CurrentSnapshotAsync();
        var seen = new List<string>();
        long? afterKey = null;
        long? afterSeq = null;

        while (true)
        {
            var page = await query.QueryAsync(new RecordQuery
            {
                Snapshot   = snapshot,
                Limit      = 5,
                Descending = true,
                AfterKey   = afterKey,
                AfterSeq   = afterSeq,
                PeerHash   = peerHash
            });
            if (page.Count == 0) break;

            seen.AddRange(page.Select(r => r.RecordId));
            afterKey = page[^1].OccurredAt;
            afterSeq = page[^1].Seq;
        }

        Assert.Equal(totalRecords, seen.Count);
        Assert.Equal(totalRecords, seen.Distinct().Count());
    }

    [Fact]
    public async Task Query_filters_by_peer_kind_and_occurred_at_range()
    {
        var peerHash1 = $"peer_f1_{Guid.NewGuid():N}";
        var peerHash2 = $"peer_f2_{Guid.NewGuid():N}";

        await using var db = _fixture.CreateContext();
        var sync  = new SyncService(db);
        var query = new RecordQueryService(db);

        // peer1: 3 deleted (occurred at 100, 200, 300) va 2 edited (occurred at 150, 250)
        await sync.PushAsync("test-device", [
            Make(peerHash1, 1, 100, RecordKind.Deleted),
            Make(peerHash1, 2, 200, RecordKind.Deleted),
            Make(peerHash1, 3, 300, RecordKind.Deleted),
            Make(peerHash1, 4, 150, RecordKind.Edited),
            Make(peerHash1, 5, 250, RecordKind.Edited),
            // peer2: 1 deleted (occurred at 200)
            Make(peerHash2, 6, 200, RecordKind.Deleted)
        ]);

        var snapshot = await query.CurrentSnapshotAsync();

        // 1. Peer + Kind filter
        var deletedOnly = await query.QueryAsync(new RecordQuery
        {
            Snapshot = snapshot,
            PeerHash = peerHash1,
            Kind     = RecordKind.Deleted,
            Limit    = 50
        });
        Assert.Equal(3, deletedOnly.Count);
        Assert.All(deletedOnly, r => Assert.Equal(RecordKind.Deleted, r.Kind));

        // 2. OccurredAt range filter (150 dan 250 gacha)
        var rangeQuery = await query.QueryAsync(new RecordQuery
        {
            Snapshot       = snapshot,
            PeerHash       = peerHash1,
            FromOccurredAt = 150,
            ToOccurredAt   = 250,
            Limit          = 50
        });
        // 150 (edited), 200 (deleted), 250 (edited)
        Assert.Equal(3, rangeQuery.Count);
        Assert.Contains(rangeQuery, r => r.OccurredAt == 150);
        Assert.Contains(rangeQuery, r => r.OccurredAt == 200);
        Assert.Contains(rangeQuery, r => r.OccurredAt == 250);
    }

    [Fact]
    public async Task Stats_storage_reflects_pushed_records_and_media()
    {
        await using var db = _fixture.CreateContext();
        var sync  = new SyncService(db);
        var media = new MediaService(db, Path.Combine(Path.GetTempPath(), $"stats-media-{Guid.NewGuid():N}"));
        var stats = new StatsService(db);

        var before = await stats.StorageAsync();

        var peerHash = $"peer_st_{Guid.NewGuid():N}";
        var mediaHash = $"media_st_{Guid.NewGuid():N}";

        // 2 ta yozuv qo'shamiz (har biri 50 bayt)
        await sync.PushAsync("test-device", [
            Make(peerHash, 1, 100, RecordKind.Edited) with { Payload = new byte[50] },
            Make(peerHash, 2, 200, RecordKind.Edited) with { Payload = new byte[50] }
        ]);

        // 1 ta media blob qo'shamiz (75 bayt)
        await media.StoreAsync(mediaHash, new byte[75], new byte[12]);

        var after = await stats.StorageAsync();

        Assert.Equal(before.RecordCount + 2, after.RecordCount);
        Assert.Equal(before.RecordBytes + 100, after.RecordBytes);
        Assert.Equal(before.MediaCount + 1, after.MediaCount);
        Assert.Equal(before.MediaBytes + 75, after.MediaBytes);
    }
}

