using System.IO.Compression;
using System.Text.Json;
using CustomSync.Core.Contracts;

namespace CustomSync.Core.Interchange;

public static class CmxReader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public const int SupportedFormatVersion = 1;

    public static async Task<(
        CmxManifest Manifest,
        IReadOnlyList<SyncRecord> Records,
        IReadOnlyDictionary<string, byte[]> Media)> ReadAsync(
            Stream input, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.json topilmadi");

        CmxManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<CmxManifest>(stream, Json, ct)
                ?? throw new InvalidDataException("manifest.json o'qib bo'lmadi");

        if (manifest.FormatVersion > SupportedFormatVersion)
            throw new InvalidDataException(
                $"Format versiyasi {manifest.FormatVersion} qo'llab-quvvatlanmaydi " +
                $"(maksimal {SupportedFormatVersion}). Serverni yangilang.");

        var records = new List<SyncRecord>(manifest.RecordCount);
        var recordsEntry = zip.GetEntry("records.jsonl")
            ?? throw new InvalidDataException("records.jsonl topilmadi");

        await using (var stream = recordsEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonSerializer.Deserialize<SyncRecord>(line, Json);
                if (record is not null) records.Add(record);
            }
        }

        var media = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("media/")))
        {
            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            media[entry.Name] = buffer.ToArray();
        }

        return (manifest, records, media);
    }
}
