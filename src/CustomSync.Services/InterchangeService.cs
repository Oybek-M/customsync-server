using CustomSync.Core.Contracts;
using CustomSync.Core.Interchange;

namespace CustomSync.Services;

/// <summary>
/// Import ATAYLAB SyncService.PushAsync ni qayta ishlatadi. Bu yerda
/// alohida merge logikasi yo'q — bo'lsa, u sync yo'lidan chetga chiqib
/// ketishi va ikkalasi turlicha xatti-harakat qilishi muqarrar edi.
/// </summary>
public class InterchangeService(SyncService sync, MediaService media)
{
    public async Task<IReadOnlyList<PushResult>> ImportAsync(
        Stream cmx, string importingDeviceId, CancellationToken ct = default)
    {
        var (manifest, records, blobs) = await CmxReader.ReadAsync(cmx, ct);

        // Media avval — yozuvlar unga havola qiladi.
        // Import qilayotgan qurilma ID'si beriladi — kvota o'sha qurilmaga yozilishi uchun.
        foreach (var (hash, content) in blobs)
            await media.StoreAsync(hash, content, new byte[12], deviceId: importingDeviceId, ct: ct);

        // Yozuvning o'z device_id si emas, import qilayotgan qurilma
        // yoziladi: kim import qilgani audit uchun muhim.
        return await sync.PushAsync(importingDeviceId, records, ct);
    }
}
