using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class ReleaseUploadTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _tempStorageRoot;

    public ReleaseUploadTests(WebApplicationFactory<Program> factory)
    {
        _tempStorageRoot = Path.Combine(Path.GetTempPath(), "cs-rel-test-" + Guid.NewGuid().ToString("N"));
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:ReleasesRoot"] = _tempStorageRoot
                });
            });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempStorageRoot))
        {
            try { Directory.Delete(_tempStorageRoot, recursive: true); } catch { }
        }
    }

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "admin")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, "test-" + Guid.NewGuid().ToString("N"), "release-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    [Fact]
    public async Task Upload_ResumesAfterInterruption()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        var version = (long)Random.Shared.Next(10000000, 90000000);
        var totalSize = 10_000_000;
        var data = new byte[totalSize];
        new Random(42).NextBytes(data);
        var shaHex = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // 1. Create release
        var createResp = await client.PostAsJsonAsync("/api/v1/releases", new
        {
            platform = "win64",
            version = version,
            channel = "stable",
            sha256 = shaHex,
            size = (long)totalSize,
            package_name = $"tx64upd{version}"
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var releaseId = createJson.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(releaseId));
        Assert.Equal("created", createJson.GetProperty("state").GetString());

        // 2. Open upload session
        var uploadResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/upload", new
        {
            size = (long)totalSize
        });
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadJson = await uploadResp.Content.ReadFromJsonAsync<JsonElement>();
        var sid = uploadJson.GetProperty("sid").GetString();
        Assert.False(string.IsNullOrEmpty(sid));
        Assert.Equal(0, uploadJson.GetProperty("received").GetInt64());

        // 3. Upload chunk 1: 4 MB (0 - 3999999)
        var chunk1Size = 4_000_000;
        using var chunk1Content = new ByteArrayContent(data, 0, chunk1Size);
        chunk1Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        chunk1Content.Headers.Add("Content-Range", $"bytes 0-{chunk1Size - 1}/{totalSize}");

        var put1Resp = await client.PutAsync($"/api/v1/releases/{releaseId}/upload/{sid}", chunk1Content);
        Assert.Equal(HttpStatusCode.OK, put1Resp.StatusCode);
        var put1Json = await put1Resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(chunk1Size, put1Json.GetProperty("received").GetInt64());

        // 4. Interruption simulation: check status with GET
        var statusResp = await client.GetAsync($"/api/v1/releases/{releaseId}/upload/{sid}");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusJson = await statusResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(chunk1Size, statusJson.GetProperty("received").GetInt64());

        // 5. Upload chunk 2: remaining 6 MB (4000000 - 9999999)
        var chunk2Size = totalSize - chunk1Size;
        using var chunk2Content = new ByteArrayContent(data, chunk1Size, chunk2Size);
        chunk2Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        chunk2Content.Headers.Add("Content-Range", $"bytes {chunk1Size}-{totalSize - 1}/{totalSize}");

        var put2Resp = await client.PutAsync($"/api/v1/releases/{releaseId}/upload/{sid}", chunk2Content);
        Assert.Equal(HttpStatusCode.OK, put2Resp.StatusCode);
        var put2Json = await put2Resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(totalSize, put2Json.GetProperty("received").GetInt64());

        // 6. Finish upload
        var finishResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/finish", new
        {
            sha256 = shaHex
        });
        var finishBody = await finishResp.Content.ReadAsStringAsync();
        Assert.True(finishResp.IsSuccessStatusCode, finishBody);
        var finishJson = JsonSerializer.Deserialize<JsonElement>(finishBody);
        Assert.Equal("complete", finishJson.GetProperty("state").GetString());

        // 7. Idempotent re-creation returns already_exists
        var recreateResp = await client.PostAsJsonAsync("/api/v1/releases", new
        {
            platform = "win64",
            version = version,
            channel = "stable",
            sha256 = shaHex,
            size = (long)totalSize,
            package_name = $"tx64upd{version}"
        });
        Assert.Equal(HttpStatusCode.OK, recreateResp.StatusCode);
        var recreateJson = await recreateResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(releaseId, recreateJson.GetProperty("id").GetString());
        Assert.Equal("already_exists", recreateJson.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Upload_RejectsInvalidContentRangeOffset_Returns416()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        var version = (long)Random.Shared.Next(10000000, 90000000);
        var totalSize = 1000;
        var data = new byte[totalSize];

        var createResp = await client.PostAsJsonAsync("/api/v1/releases", new
        {
            platform = "linux",
            version = version,
            channel = "stable",
            sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            size = (long)totalSize,
            package_name = $"tlinuxupd{version}"
        });
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var releaseId = createJson.GetProperty("id").GetString();

        var uploadResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/upload", new { size = (long)totalSize });
        var uploadJson = await uploadResp.Content.ReadFromJsonAsync<JsonElement>();
        var sid = uploadJson.GetProperty("sid").GetString();

        // Range start is 500 but server has 0 bytes -> must return 416
        using var chunkContent = new ByteArrayContent(data, 0, 500);
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        chunkContent.Headers.Add("Content-Range", $"bytes 500-999/{totalSize}");

        var putResp = await client.PutAsync($"/api/v1/releases/{releaseId}/upload/{sid}", chunkContent);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, putResp.StatusCode);
    }

    [Fact]
    public async Task Finish_RejectsWrongChecksum_Returns422_DeletesTempFile()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        var version = (long)Random.Shared.Next(10000000, 90000000);
        var totalSize = 500;
        var data = new byte[totalSize];
        new Random(99).NextBytes(data);
        var realSha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var createResp = await client.PostAsJsonAsync("/api/v1/releases", new
        {
            platform = "macos",
            version = version,
            channel = "beta",
            sha256 = realSha,
            size = (long)totalSize,
            package_name = $"tmacupd{version}"
        });
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var releaseId = createJson.GetProperty("id").GetString();

        var uploadResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/upload", new { size = (long)totalSize });
        var uploadJson = await uploadResp.Content.ReadFromJsonAsync<JsonElement>();
        var sid = uploadJson.GetProperty("sid").GetString();

        using var chunkContent = new ByteArrayContent(data);
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        chunkContent.Headers.Add("Content-Range", $"bytes 0-{totalSize - 1}/{totalSize}");
        var putResp = await client.PutAsync($"/api/v1/releases/{releaseId}/upload/{sid}", chunkContent);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Finish with wrong sha256
        var wrongSha = "0000000000000000000000000000000000000000000000000000000000000000";
        var finishResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/finish", new { sha256 = wrongSha });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, finishResp.StatusCode);

        // Temp file should be deleted and session removed
        var statusResp = await client.GetAsync($"/api/v1/releases/{releaseId}/upload/{sid}");
        Assert.Equal(HttpStatusCode.NotFound, statusResp.StatusCode);
    }

    [Fact]
    public async Task Publish_And_GetReleases_WorkCorrectly()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        var version = (long)Random.Shared.Next(10000000, 90000000);
        var totalSize = 200;
        var data = new byte[totalSize];
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // 1. Create release
        var createResp = await client.PostAsJsonAsync("/api/v1/releases", new
        {
            platform = "win64",
            version = version,
            channel = "alpha",
            sha256 = sha,
            size = (long)totalSize,
            package_name = $"tx64upd{version}"
        });
        var releaseId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // 2. Upload and finish
        var uploadResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/upload", new { size = (long)totalSize });
        var sid = (await uploadResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString();

        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("Content-Range", $"bytes 0-{totalSize - 1}/{totalSize}");
        await client.PutAsync($"/api/v1/releases/{releaseId}/upload/{sid}", content);
        await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/finish", new { sha256 = sha });

        // 3. Publish to all mirrors
        var publishResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/publish", new { });
        Assert.Equal(HttpStatusCode.OK, publishResp.StatusCode);
        var pubJson = await publishResp.Content.ReadFromJsonAsync<JsonElement>();
        var mirrors = pubJson.GetProperty("mirrors").EnumerateArray().ToList();
        Assert.True(mirrors.Count >= 3);
        Assert.Contains(mirrors, m => m.GetProperty("mirror").GetString() == "vps-secure");
        Assert.Contains(mirrors, m => m.GetProperty("mirror").GetString() == "vps-pub");
        Assert.Contains(mirrors, m => m.GetProperty("mirror").GetString() == "github");

        // 4. Publish with ?only=vps-secure
        var onlyResp = await client.PostAsJsonAsync($"/api/v1/releases/{releaseId}/publish?only=vps-secure", new { });
        Assert.Equal(HttpStatusCode.OK, onlyResp.StatusCode);
        var onlyJson = await onlyResp.Content.ReadFromJsonAsync<JsonElement>();
        var onlyMirrors = onlyJson.GetProperty("mirrors").EnumerateArray().ToList();
        Assert.Single(onlyMirrors);
        Assert.Equal("vps-secure", onlyMirrors[0].GetProperty("mirror").GetString());

        // 5. GET /api/v1/releases
        var listResp = await client.GetAsync("/api/v1/releases");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var releasesList = listJson.EnumerateArray().ToList();
        Assert.Contains(releasesList, r => r.GetProperty("release_id").GetString() == releaseId);
    }
}
