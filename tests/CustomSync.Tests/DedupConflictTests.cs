using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Spec §3.4 dedup konflikt qoidasi: bir xil `record_id` ikki marta
/// kelsa, `observed_at` KICHIGI g'olib. Sababi — eng erta kuzatgan
/// qurilma xabarni eng to'liq ushlagan bo'ladi (desktop o'chirilishidan
/// oldin ko'rgan, telefon esa faqat keyin ulangan).
///
/// Bu qoida `SyncService.UpsertSql` dagi `ON CONFLICT ... WHERE` da
/// yashiringan. Taqqoslash yo'nalishi teskari bo'lsa, server yaxshiroq
/// nusxani yomoni bilan jimgina almashtiradi va buni hech qanday
/// xato ko'rsatmaydi — aynan shu plan oldini olmoqchi bo'lgan
/// "jimgina yo'qotish" turi.
/// </summary>
public class DedupConflictTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public DedupConflictTests(DatabaseFixture fixture) => _fixture = fixture;

    private const string Account = "acc01";
    private const long   Occurred = 1753800000;

    /// <summary>
    /// Bir xil `record_id`, faqat `observed_at` va `device_id` farq
    /// qiladi -- `record_id` formulasiga ular kirmaydi.
    /// </summary>
    private static SyncRecord Make(
        string peerHash, long observedAt, string deviceId, byte payloadMarker) => new()
    {
        RecordId    = RecordId.Compute(RecordKind.Deleted, Account, peerHash, 1, Occurred),
        Kind        = RecordKind.Deleted,
        AccountHash = Account,
        PeerHash    = peerHash,
        MsgId       = 1,
        OccurredAt  = Occurred,
        ObservedAt  = observedAt,
        DeviceId    = deviceId,
        Nonce       = new byte[12],
        Payload     = [payloadMarker]
    };

    private async Task<StoredRecord> SingleStoredAsync(string peerHash)
    {
        await using var db = _fixture.CreateContext();
        var page = await new SyncService(db).PullAsync(0, 500);
        return Assert.Single(page.Records.Where(r => r.PeerHash == peerHash));
    }

    [Fact]
    public async Task Earlier_observation_supersedes_the_stored_one()
    {
        var peerHash = $"peer_lww_a_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var service = new SyncService(db);

        await service.PushAsync("late-device", [Make(peerHash, 2000, "late-device", 1)]);
        var results = await service.PushAsync(
            "early-device", [Make(peerHash, 1000, "early-device", 2)]);

        Assert.Equal(PushOutcome.Superseded, results[0].Status);

        var stored = await SingleStoredAsync(peerHash);
        Assert.Equal(1000, stored.ObservedAt);
        Assert.Equal(2, stored.Payload[0]);
    }

    [Fact]
    public async Task Later_observation_is_rejected_as_a_duplicate()
    {
        var peerHash = $"peer_lww_b_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var service = new SyncService(db);

        await service.PushAsync("early-device", [Make(peerHash, 1000, "early-device", 1)]);
        var results = await service.PushAsync(
            "late-device", [Make(peerHash, 2000, "late-device", 2)]);

        Assert.Equal(PushOutcome.Duplicate, results[0].Status);

        // Saqlangan nusxa TEGILMAGAN bo'lishi kerak.
        var stored = await SingleStoredAsync(peerHash);
        Assert.Equal(1000, stored.ObservedAt);
        Assert.Equal(1, stored.Payload[0]);
    }

    /// <summary>
    /// Spec §3.4: `observed_at` teng bo'lsa `device_id` leksikografik
    /// kichigi g'olib. Sof determinizm uchun -- barcha qurilmalar
    /// yetib kelish tartibidan qat'i nazar bir xil natijaga kelsin.
    /// </summary>
    [Fact]
    public async Task Equal_observations_are_broken_by_the_smaller_device_id()
    {
        var peerHash = $"peer_lww_c_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var service = new SyncService(db);

        await service.PushAsync("zzz-device", [Make(peerHash, 1500, "zzz-device", 1)]);
        var results = await service.PushAsync(
            "aaa-device", [Make(peerHash, 1500, "aaa-device", 2)]);

        Assert.Equal(PushOutcome.Superseded, results[0].Status);

        var stored = await SingleStoredAsync(peerHash);
        Assert.Equal("aaa-device", stored.DeviceId);
    }

    /// <summary>
    /// Spec §3.4: almashtirilgan yozuv YANGI `seq` oladi. Boshqa
    /// qurilmalar eski nusxani allaqachon tortib olgan bo'lishi
    /// mumkin -- yangi `seq` ularni tuzatilgan nusxani qayta tortib
    /// olishga majbur qiladi. `seq` o'zgarmasa, ular eski nusxa bilan
    /// abadiy qolib ketardi.
    /// </summary>
    [Fact]
    public async Task Superseding_assigns_a_new_seq_so_readers_refetch()
    {
        var peerHash = $"peer_lww_d_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var service = new SyncService(db);

        await service.PushAsync("late-device", [Make(peerHash, 2000, "late-device", 1)]);
        var firstSeq = (await SingleStoredAsync(peerHash)).Seq;

        await service.PushAsync("early-device", [Make(peerHash, 1000, "early-device", 2)]);
        var secondSeq = (await SingleStoredAsync(peerHash)).Seq;

        Assert.True(secondSeq > firstSeq,
            $"almashtirilgandan keyin seq oshishi kerak edi: {firstSeq} -> {secondSeq}");
    }
}
