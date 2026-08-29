using System.Security.Claims;
using CustomSync.Core.Contracts;
using CustomSync.Core.Interchange;
using CustomSync.Data;
using CustomSync.Services;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Api.Endpoints;

public static class InterchangeEndpoints
{
    public static void MapInterchangeEndpoints(this WebApplication app)
    {
        // Import va eksport web app ma'muriy boshqaruvi uchun — faqat admin (Correction 4)
        var group = app.MapGroup("/api/v1").RequireAuthorization("admin");

        group.MapPost("/import", async (
            HttpRequest request, ClaimsPrincipal user, InterchangeService interchange, CancellationToken ct) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            try
            {
                var results = await interchange.ImportAsync(buffer, deviceId, ct);
                return Results.Ok(new
                {
                    imported   = results.Count,
                    created    = results.Count(r => r.Status == PushOutcome.Created),
                    duplicates = results.Count(r => r.Status == PushOutcome.Duplicate),
                    errors     = results.Count(r => r.Status == PushOutcome.Error)
                });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = "invalid_cmx", message = ex.Message });
            }
        }).DisableAntiforgery();

        group.MapGet("/export", async (
            long? since, long? until, string? peerHash,
            ClaimsPrincipal user, SyncDbContext db, CancellationToken ct) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

            // Eksport boshlangan paytdagi max(seq) snapshot sifatida olinadi (Correction 2).
            var snapshot = await db.Records.MaxAsync(r => (long?)r.Seq, ct) ?? 0L;

            var q = db.Records.AsNoTracking().Where(r => r.Seq <= snapshot);
            if (since is not null)    q = q.Where(r => r.OccurredAt >= since);
            if (until is not null)    q = q.Where(r => r.OccurredAt <= until);
            if (peerHash is not null) q = q.Where(r => r.PeerHash == peerHash);

            // Ma'lumotlar bazasida keyset sahifalash orqali barcha mos yozuvlarni olamiz (K3)
            var records = new List<SyncRecord>();
            long? afterKey = null;
            long? afterSeq = null;
            const int batchSize = 200;

            while (true)
            {
                var pageQ = q;
                if (afterKey is not null && afterSeq is not null)
                {
                    var key = afterKey.Value;
                    var seq = afterSeq.Value;
                    pageQ = pageQ.Where(r => r.OccurredAt > key || (r.OccurredAt == key && r.Seq > seq));
                }

                var batch = await pageQ
                    .OrderBy(r => r.OccurredAt).ThenBy(r => r.Seq)
                    .Take(batchSize)
                    .Select(r => new
                    {
                        r.Seq,
                        Record = new SyncRecord
                        {
                            RecordId       = r.RecordId,
                            Kind           = r.Kind,
                            AccountHash    = r.AccountHash,
                            PeerHash       = r.PeerHash,
                            MsgId          = r.MsgId,
                            OccurredAt     = r.OccurredAt,
                            ObservedAt     = r.ObservedAt,
                            DeviceId       = r.DeviceId,
                            Nonce          = r.Nonce,
                            Payload        = r.Payload,
                            TargetRecordId = r.TargetRecordId
                        }
                    })
                    .ToListAsync(ct);

                if (batch.Count == 0) break;

                records.AddRange(batch.Select(b => b.Record));
                afterKey = batch[^1].Record.OccurredAt;
                afterSeq = batch[^1].Seq;
            }

            var output = new MemoryStream();
            // Server eksportida media bloblar kiritilmaydi (Correction 5: spec §0.7 bo'yicha
            // media alohida arxivda saqlanishi mumkin va server xotirasi tejab qolinadi).
            await CmxWriter.WriteAsync(output, new CmxManifest
            {
                FormatVersion  = CmxReader.SupportedFormatVersion,
                SourceApp      = "server-backend",
                DeviceId       = deviceId,
                CreatedAt      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RecordCount    = records.Count,
                Encrypted      = true,
                KeyFingerprint = "server-unknown",
                ScopeSince     = since,
                ScopeUntil     = until,
                ScopePeerHash  = peerHash
            }, records, media: new Dictionary<string, byte[]>(), ct: ct);

            output.Position = 0;
            var name = $"customsync-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cmx";
            return Results.File(output, "application/zip", name);
        });
    }
}
