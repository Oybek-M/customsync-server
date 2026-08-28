using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class RecordEndpoints
{
    public static void MapRecordEndpoints(this WebApplication app)
    {
        // Web app boshqaruv paneli uchun — faqat admin ruxsat etiladi (Correction 4)
        var group = app.MapGroup("/api/v1/records").RequireAuthorization("admin");

        group.MapGet("/snapshot", async (RecordQueryService query) =>
            Results.Ok(new { snapshot = await query.CurrentSnapshotAsync() }));

        group.MapGet("/", async (
            long snapshot, int? limit, bool? desc,
            long? afterKey, long? afterSeq,
            string? peerHash, string? kind, long? from, long? to,
            RecordQueryService query, SettingsService settings) =>
        {
            var defaultSize = await settings.GetIntAsync("api.default_page_size");
            var maxSize     = await settings.GetIntAsync("api.max_page_size");

            var rows = await query.QueryAsync(new RecordQuery
            {
                Snapshot       = snapshot,
                Limit          = Math.Clamp(limit ?? defaultSize, 1, maxSize),
                Descending     = desc ?? true,
                AfterKey       = afterKey,
                AfterSeq       = afterSeq,
                PeerHash       = peerHash,
                Kind           = kind,
                FromOccurredAt = from,
                ToOccurredAt   = to
            });

            return Results.Ok(new
            {
                records = rows,
                nextAfterKey = rows.Count > 0 ? rows[^1].OccurredAt : (long?)null,
                nextAfterSeq = rows.Count > 0 ? rows[^1].Seq : (long?)null
            });
        });

        group.MapGet("/{recordId}/payload", async (
            string recordId, RecordQueryService query) =>
        {
            var payload = await query.GetPayloadAsync(recordId);
            return payload is null
                ? Results.NotFound()
                : Results.File(payload, "application/octet-stream");
        });
    }
}
