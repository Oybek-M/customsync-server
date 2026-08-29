using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CustomSync.Core.Contracts;

namespace CustomSync.Core.Interchange;

/// <summary>
/// .cmx — oddiy ZIP. records.jsonl satrlari HTTP sync payload bilan
/// AYNAN bir xil shaklga ega, shuning uchun import push bilan bir xil
/// kodni bosadi va alohida sinovdan o'tkazilishi shart emas.
/// </summary>
public static class CmxWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static async Task WriteAsync(
        Stream output,
        CmxManifest manifest,
        IReadOnlyList<SyncRecord> records,
        IReadOnlyDictionary<string, byte[]> media,
        CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = zip.CreateEntry("manifest.json");
        await using (var stream = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(stream, manifest, Json, ct);

        var recordsEntry = zip.CreateEntry("records.jsonl");
        await using (var stream = recordsEntry.Open())
        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            foreach (var record in records)
                await writer.WriteLineAsync(JsonSerializer.Serialize(record, Json));
        }

        foreach (var (hash, content) in media)
        {
            var entry = zip.CreateEntry($"media/{hash}");
            await using var stream = entry.Open();
            await stream.WriteAsync(content, ct);
        }
    }
}
