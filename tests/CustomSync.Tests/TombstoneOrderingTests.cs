using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Spec §0.3 + §0.13: tombstone GLOBAL o'chirish. Qurilmalar mustaqil
/// sync qiladi, shuning uchun tombstone o'z nishonidan OLDIN kelishi
/// oddiy hol: telefon xabarni o'chiradi va tombstone push qiladi,
/// noutbuk esa hali asl yozuvni push qilmagan bo'ladi.
///
/// Bunday holda tombstone'ni shunchaki saqlash yetarli EMAS. Nishon
/// keyinroq kelganda u o'chirilishi shart — aks holda o'chirish
/// jimgina bekor bo'ladi va yozuv abadiy tirilib qoladi.
/// </summary>
public class TombstoneOrderingTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public TombstoneOrderingTests(DatabaseFixture fixture) => _fixture = fixture;

    private const string Account  = "acc01";
    private const long   Occurred = 1753800000;

    private static SyncRecord Target(string peerHash) => new()
    {
        RecordId    = RecordId.Compute(RecordKind.Deleted, Account, peerHash, 7, Occurred),
        Kind        = RecordKind.Deleted,
        AccountHash = Account,
        PeerHash    = peerHash,
        MsgId       = 7,
        OccurredAt  = Occurred,
        ObservedAt  = Occurred,
        DeviceId    = "laptop",
        Nonce       = new byte[12],
        Payload     = [1]
    };

    private static SyncRecord Tombstone(string peerHash, string targetRecordId) => new()
    {
        RecordId       = RecordId.Compute(RecordKind.Tombstone, Account, peerHash, 99, Occurred),
        Kind           = RecordKind.Tombstone,
        AccountHash    = Account,
        PeerHash       = peerHash,
        MsgId          = 99,
        OccurredAt     = Occurred,
        ObservedAt     = Occurred,
        DeviceId       = "phone",
        Nonce          = new byte[12],
        Payload        = [2],
        TargetRecordId = targetRecordId
    };

    private async Task<IReadOnlyList<StoredRecord>> StoredAsync(string peerHash)
    {
        await using var db = _fixture.CreateContext();
        var page = await new SyncService(db).PullAsync(0, 500);
        return page.Records.Where(r => r.PeerHash == peerHash).ToList();
    }

    /// <summary>
    /// Nishon OLDIN kelgan holat — bu allaqachon ishlaydi, referens
    /// sifatida turadi.
    /// </summary>
    [Fact]
    public async Task Tombstone_after_target_deletes_it()
    {
        var peerHash = $"peer_tomb_a_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var sync = new SyncService(db);
        var target = Target(peerHash);

        await sync.PushAsync("laptop", [target]);
        await sync.PushAsync("phone", [Tombstone(peerHash, target.RecordId)]);

        var stored = await StoredAsync(peerHash);
        Assert.DoesNotContain(stored, r => r.RecordId == target.RecordId);
        Assert.Contains(stored, r => r.Kind == RecordKind.Tombstone);
    }

    /// <summary>
    /// Nishon KEYIN kelgan holat. Qurilmalar mustaqil sync qilgani
    /// uchun bu tartib butunlay normal — va aynan shu yerda o'chirish
    /// jimgina yo'qolishi mumkin.
    /// </summary>
    [Fact]
    public async Task Target_arriving_after_its_tombstone_is_still_deleted()
    {
        var peerHash = $"peer_tomb_b_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var sync = new SyncService(db);
        var target = Target(peerHash);

        // Tombstone birinchi bo'lib keladi -- nishon hali yo'q.
        await sync.PushAsync("phone", [Tombstone(peerHash, target.RecordId)]);

        // Endi nishon keladi. U DARHOL o'chirilishi kerak.
        await sync.PushAsync("laptop", [target]);

        var stored = await StoredAsync(peerHash);
        Assert.DoesNotContain(stored, r => r.RecordId == target.RecordId);
        Assert.Contains(stored, r => r.Kind == RecordKind.Tombstone);
    }

    /// <summary>
    /// K4: bir xil tombstone qayta yuborilsa natija o'zgarmasligi kerak.
    /// </summary>
    [Fact]
    public async Task Replaying_a_tombstone_changes_nothing()
    {
        var peerHash = $"peer_tomb_c_{Guid.NewGuid():N}";
        await using var db = _fixture.CreateContext();
        var sync = new SyncService(db);
        var target = Target(peerHash);
        var tomb   = Tombstone(peerHash, target.RecordId);

        await sync.PushAsync("laptop", [target]);
        await sync.PushAsync("phone", [tomb]);
        await sync.PushAsync("phone", [tomb]);

        var stored = await StoredAsync(peerHash);
        Assert.Single(stored);
        Assert.Equal(RecordKind.Tombstone, stored[0].Kind);
    }
}
