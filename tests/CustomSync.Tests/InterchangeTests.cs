using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Core.Interchange;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class InterchangeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InterchangeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "admin")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "interchange-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    private static SyncRecord MakeRecord(string peerHash, int index, string kind = RecordKind.Edited, string? targetRecordId = null) => new()
    {
        RecordId       = RecordId.Compute(kind, "acc01", peerHash, index, 1753900000 + index),
        Kind           = kind,
        AccountHash    = "acc01",
        PeerHash       = peerHash,
        MsgId          = index,
        OccurredAt     = 1753900000 + index,
        ObservedAt     = 1753900001 + index,
        DeviceId       = "exporter",
        Nonce          = new byte[12],
        Payload        = [7, 7, 7],
        TargetRecordId = targetRecordId
    };

    [Fact]
    public async Task Round_trip_preserves_every_record_field_including_account_hash()
    {
        var peerHash = $"peer_rt_{Guid.NewGuid():N}";
        var original = Enumerable.Range(0, 5).Select(i => MakeRecord(peerHash, i)).ToList();
        using var stream = new MemoryStream();

        await CmxWriter.WriteAsync(stream, new CmxManifest
        {
            FormatVersion  = 1,
            SourceApp      = "test",
            DeviceId       = "exporter",
            CreatedAt      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RecordCount    = original.Count,
            Encrypted      = true,
            KeyFingerprint = "abcd1234"
        }, original, media: new Dictionary<string, byte[]>());

        stream.Position = 0;
        var (manifest, records, _) = await CmxReader.ReadAsync(stream);

        Assert.Equal(5, manifest.RecordCount);
        Assert.Equal(original.Select(r => r.RecordId), records.Select(r => r.RecordId));
        Assert.Equal(original[0].AccountHash, records[0].AccountHash);
        Assert.Equal(original[0].Payload, records[0].Payload);
        Assert.Equal(original[0].ObservedAt, records[0].ObservedAt);
    }

    [Fact]
    public async Task Import_produces_the_same_state_as_push()
    {
        using var scope = _factory.Services.CreateScope();
        var sync        = scope.ServiceProvider.GetRequiredService<SyncService>();
        var media       = scope.ServiceProvider.GetRequiredService<MediaService>();
        var interchange = new InterchangeService(sync, media);
        var query       = scope.ServiceProvider.GetRequiredService<RecordQueryService>();

        var peerHash = $"peer_same_{Guid.NewGuid():N}";
        var records = Enumerable.Range(10, 5).Select(i => MakeRecord(peerHash, i)).ToList();

        // 1. Push orqali kiritish
        await sync.PushAsync("exporter", records);
        var snapshot = await query.CurrentSnapshotAsync();
        var afterPush = await query.QueryAsync(
            new RecordQuery { Snapshot = snapshot, PeerHash = peerHash, Limit = 100 });

        // 2. Xuddi shu yozuvlarni .cmx orqali kiritish (idempotent duplicate bo'lishi kerak)
        using var stream = new MemoryStream();
        await CmxWriter.WriteAsync(stream, new CmxManifest
        {
            FormatVersion  = 1,
            SourceApp      = "test",
            DeviceId       = "exporter",
            CreatedAt      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RecordCount    = records.Count,
            Encrypted      = true,
            KeyFingerprint = "abcd1234"
        }, records, media: new Dictionary<string, byte[]>());
        stream.Position = 0;

        var imported = await interchange.ImportAsync(stream, "importer");

        var afterImport = await query.QueryAsync(
            new RecordQuery { Snapshot = snapshot, PeerHash = peerHash, Limit = 100 });

        Assert.Equal(records.Count, imported.Count);
        Assert.Equal(afterPush.Count, afterImport.Count);
        Assert.All(imported, r => Assert.Equal(PushOutcome.Duplicate, r.Status));
    }

    [Fact]
    public async Task Tombstone_survives_export_and_re_import_with_target_record_id_intact()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        using var scope = _factory.Services.CreateScope();
        var sync  = scope.ServiceProvider.GetRequiredService<SyncService>();
        var query = scope.ServiceProvider.GetRequiredService<RecordQueryService>();

        var peerHash = $"peer_tomb_{Guid.NewGuid():N}";

        // Asl xabar yozuvi
        var original = MakeRecord(peerHash, 1, RecordKind.Edited);
        // Tombstone yozuvi (TargetRecordId = original.RecordId)
        var tombstone = MakeRecord(peerHash, 2, RecordKind.Tombstone, targetRecordId: original.RecordId);

        // 1. Ikkalasini ham push qilamiz
        await sync.PushAsync("test-device", [original, tombstone]);

        // 2. Export qilamiz (/api/v1/export?peerHash=...)
        var exportResp = await adminClient.GetAsync($"/api/v1/export?peerHash={peerHash}");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        var cmxBytes = await exportResp.Content.ReadAsByteArrayAsync();

        // 3. Export qilingan .cmx ni o'qiymiz va tombstone'da TargetRecordId mavjudligini tekshiramiz
        using var stream = new MemoryStream(cmxBytes);
        var (_, exportedRecords, _) = await CmxReader.ReadAsync(stream);

        var exportedTombstone = exportedRecords.FirstOrDefault(r => r.Kind == RecordKind.Tombstone);
        Assert.NotNull(exportedTombstone);
        Assert.Equal(original.RecordId, exportedTombstone.TargetRecordId);

        // 4. Re-import qilamiz va xatosiz qabul qilinishini (missing_target xatosi bo'lmasligini) tekshiramiz
        stream.Position = 0;
        using var content = new ByteArrayContent(cmxBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var importResp = await adminClient.PostAsync("/api/v1/import", content);
        Assert.Equal(HttpStatusCode.OK, importResp.StatusCode);

        var json = await importResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task Manifest_with_unsupported_higher_format_version_is_rejected()
    {
        var original = new List<SyncRecord>();
        using var stream = new MemoryStream();

        await CmxWriter.WriteAsync(stream, new CmxManifest
        {
            FormatVersion  = 999, // Qo'llab-quvvatlanmaydigan kelajak versiyasi
            SourceApp      = "future-app",
            DeviceId       = "exporter",
            CreatedAt      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RecordCount    = 0,
            Encrypted      = true,
            KeyFingerprint = "abcd1234"
        }, original, media: new Dictionary<string, byte[]>());

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CmxReader.ReadAsync(stream));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public async Task Post_import_as_admin_with_valid_cmx_returns_200_with_counts()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");

        var peerHash = $"peer_post_imp_{Guid.NewGuid():N}";
        var records = Enumerable.Range(1, 3).Select(i => MakeRecord(peerHash, i)).ToList();

        using var stream = new MemoryStream();
        await CmxWriter.WriteAsync(stream, new CmxManifest
        {
            FormatVersion  = 1,
            SourceApp      = "test",
            DeviceId       = "exporter",
            CreatedAt      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RecordCount    = records.Count,
            Encrypted      = true,
            KeyFingerprint = "abcd1234"
        }, records, media: new Dictionary<string, byte[]>());

        using var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        var resp = await adminClient.PostAsync("/api/v1/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("imported").GetInt32());
        Assert.Equal(3, json.GetProperty("created").GetInt32());
        Assert.Equal(0, json.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task Post_import_with_invalid_zip_bytes_returns_400_invalid_cmx()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");

        using var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        var resp = await adminClient.PostAsync("/api/v1/import", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_cmx", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_export_as_admin_returns_cmx_with_pushed_record()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        using var scope = _factory.Services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        var peerHash = $"peer_exp_{Guid.NewGuid():N}";
        var record = MakeRecord(peerHash, 1);
        await sync.PushAsync("test-device", [record]);

        var resp = await adminClient.GetAsync($"/api/v1/export?peerHash={peerHash}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);

        var cmxBytes = await resp.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(cmxBytes);
        var (manifest, records, _) = await CmxReader.ReadAsync(stream);

        Assert.Equal(1, manifest.RecordCount);
        Assert.Single(records);
        Assert.Equal(record.RecordId, records[0].RecordId);
        Assert.Equal(record.AccountHash, records[0].AccountHash);
    }

    [Fact]
    public async Task Import_and_export_with_device_role_returns_403()
    {
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        // 1. GET /export -> 403 Forbidden
        var exportResp = await deviceClient.GetAsync("/api/v1/export");
        Assert.Equal(HttpStatusCode.Forbidden, exportResp.StatusCode);

        // 2. POST /import -> 403 Forbidden
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        var importResp = await deviceClient.PostAsync("/api/v1/import", content);
        Assert.Equal(HttpStatusCode.Forbidden, importResp.StatusCode);
    }
}
