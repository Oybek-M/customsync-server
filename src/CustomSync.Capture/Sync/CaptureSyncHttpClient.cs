using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CustomSync.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Sync;

public record RefreshResult(string RefreshToken, string AccessToken, long ExpiresAt);

public enum PushStatus
{
    Success,
    BatchTooLarge,
    Unauthorized,
    ServerError,
    NetworkError
}

public record PushResponse(
    PushStatus Status,
    IReadOnlyList<PushResult>? Results = null,
    int? MaxBatch = null,
    string? ErrorMessage = null);

public record KeyWrapSummary(string WrapId, string WrapType, string Label, string CreatedAt);

public enum GetWrapStatus
{
    Success,
    NotFound,
    RateLimited,
    Error
}

public record KeyWrapDetails(
    string WrapId,
    string WrapType,
    string Label,
    string Salt,
    string Nonce,
    string WrappedKey,
    int Iterations);

public record GetWrapResponse(
    GetWrapStatus Status,
    KeyWrapDetails? Wrap = null,
    string? ErrorMessage = null);

public enum PullStatus
{
    Success,
    Unauthorized,
    BadRequest,
    ServerError,
    NetworkError,
    BadJson
}

public record PullRecordsResult(
    PullStatus Status,
    PullResponse? Response = null,
    string? ErrorMessage = null);

public enum MediaUploadStatus
{
    Success,
    PayloadTooLarge, // 413
    InsufficientStorage, // 507
    Unauthorized,
    Error
}

public enum PostHealthStatus
{
    Success,
    Unauthorized,
    Error
}

/// <param name="Failed">404 ham, 200 ham emas (5xx, 400 ...): blob bor-yo'qligi NOMA'LUM —
/// bu holatni 404 deb talqin qilish keraksiz PUT'ga va noto'g'ri nonce'ga olib kelardi.</param>
public record HeadMediaResult(bool Exists, string? NonceBase64 = null, bool Unauthorized = false, bool Failed = false);
public record PutMediaResult(MediaUploadStatus Status, string? NonceBase64 = null, string? ErrorMessage = null);

public class CaptureSyncHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CaptureSyncHttpClient>? _logger;

    public CaptureSyncHttpClient(HttpClient httpClient, ILogger<CaptureSyncHttpClient>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public virtual async Task<RefreshResult?> RefreshTokenAsync(
        string serverUrl,
        string deviceId,
        string refreshToken,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/devices/refresh";
        var payload = JsonSerializer.Serialize(new
        {
            device_id = deviceId,
            refresh_token = refreshToken
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Network error during device token refresh.");
            throw;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Server refresh token'ni allaqachon almashtirgan: qolgan maydonlar
        // qanday bo'lmasin, yangi token chaqiruvchiga yetib borishi shart,
        // aks holda eskisi o'lik va qurilmani qayta enroll qilish kerak.
        var newRefreshToken = root.GetProperty("refresh_token").GetString()!;
        var newAccessToken = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString()!
            : "";
        var expiresAt = root.TryGetProperty("expires_at", out var exp) ? ParseExpiresAt(exp) : 0;

        return new RefreshResult(newRefreshToken, newAccessToken, expiresAt);
    }

    /// <summary>
    /// Server `expires_at` ni `DateTime` (ISO 8601) sifatida yuboradi
    /// (`JwtIssuer`). Son ham qabul qilinadi. Tushunarsiz qiymat → 0,
    /// ya'ni token darhol eskirgan hisoblanadi va keyingi siklda yangilanadi.
    /// </summary>
    internal static long ParseExpiresAt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
            return seconds;

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return parsed.ToUnixTimeSeconds();

        return 0;
    }

    public virtual async Task<RefreshResult?> RefreshAndPersistTokenAsync(
        string serverUrl,
        string statePath,
        DeviceState currentState,
        CancellationToken ct = default)
    {
        var refreshed = await RefreshTokenAsync(serverUrl, currentState.DeviceId, currentState.RefreshToken, ct);
        if (refreshed == null)
        {
            return null;
        }

        var newState = new DeviceState(currentState.DeviceId, refreshed.RefreshToken);
        DeviceCredentials.SaveDeviceState(statePath, newState);
        return refreshed;
    }

    public virtual async Task<IReadOnlyList<KeyWrapSummary>> ListKeyWrapsAsync(
        string serverUrl,
        string accessToken,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/keys/wraps";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var list = new List<KeyWrapSummary>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var wrapId = elem.TryGetProperty("wrap_id", out var idProp) ? idProp.GetString() ?? "" : "";
                var wrapType = elem.TryGetProperty("wrap_type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                var label = elem.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? "" : "";
                var createdAt = elem.TryGetProperty("created_at", out var dateProp) ? dateProp.GetString() ?? "" : "";
                list.Add(new KeyWrapSummary(wrapId, wrapType, label, createdAt));
            }
        }

        return list;
    }

    public virtual async Task<GetWrapResponse> GetKeyWrapAsync(
        string serverUrl,
        string accessToken,
        string wrapId,
        CancellationToken ct = default)
    {
        // wrap_id serverdan keladi (ishonchsiz): `/` yoki `?` so'rovni boshqa
        // endpoint'ga burmasligi uchun escape qilinadi.
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/keys/wraps/{Uri.EscapeDataString(wrapId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new GetWrapResponse(GetWrapStatus.RateLimited, ErrorMessage: "Hourly rate limit reached (5 requests per hour).");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GetWrapResponse(GetWrapStatus.NotFound, ErrorMessage: "Wrap not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new GetWrapResponse(GetWrapStatus.Error, ErrorMessage: $"Server returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var id = root.TryGetProperty("wrap_id", out var pId) ? pId.GetString() ?? wrapId : wrapId;
        var type = root.TryGetProperty("wrap_type", out var pType) ? pType.GetString() ?? "" : "";
        var label = root.TryGetProperty("label", out var pLabel) ? pLabel.GetString() ?? "" : "";
        var salt = root.TryGetProperty("salt", out var pSalt) ? pSalt.GetString() ?? "" : "";
        var nonce = root.TryGetProperty("nonce", out var pNonce) ? pNonce.GetString() ?? "" : "";
        var wrappedKey = root.TryGetProperty("wrapped_key", out var pWrapped) ? pWrapped.GetString() ?? "" : "";
        var iterations = root.TryGetProperty("iterations", out var pIter) ? pIter.GetInt32() : 0;

        var details = new KeyWrapDetails(id, type, label, salt, nonce, wrappedKey, iterations);
        return new GetWrapResponse(GetWrapStatus.Success, Wrap: details);
    }

    public virtual async Task<PushResponse> PushRecordsAsync(
        string serverUrl,
        string accessToken,
        IReadOnlyList<SyncRecord> records,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/sync/push";
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var payload = JsonSerializer.Serialize(new { records }, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Network error during sync push.");
            return new PushResponse(PushStatus.NetworkError, ErrorMessage: ex.Message);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new PushResponse(PushStatus.Unauthorized);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var errProp) &&
                    errProp.GetString() == "batch_too_large")
                {
                    int? max = doc.RootElement.TryGetProperty("max", out var m) ? m.GetInt32() : null;
                    return new PushResponse(PushStatus.BatchTooLarge, MaxBatch: max, ErrorMessage: "batch_too_large");
                }
            }
            catch
            {
            }
            return new PushResponse(PushStatus.ServerError, ErrorMessage: errBody);
        }

        if ((int)response.StatusCode >= 500)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            return new PushResponse(PushStatus.ServerError, ErrorMessage: $"HTTP {(int)response.StatusCode}: {errBody}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            return new PushResponse(PushStatus.ServerError, ErrorMessage: $"HTTP {(int)response.StatusCode}: {errBody}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var resDoc = JsonDocument.Parse(json);
        var resultsArray = resDoc.RootElement.GetProperty("results");

        var resultsList = new List<PushResult>();
        foreach (var elem in resultsArray.EnumerateArray())
        {
            var recordId = elem.GetProperty("record_id").GetString()!;
            var status = elem.GetProperty("status").GetString()!;
            long? seq = elem.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
            string? message = elem.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            resultsList.Add(new PushResult(recordId, status, seq, message));
        }

        return new PushResponse(PushStatus.Success, Results: resultsList);
    }

    public virtual async Task<PullRecordsResult> PullRecordsAsync(
        string serverUrl,
        string accessToken,
        long since,
        int limit,
        string? kind = null,
        CancellationToken ct = default)
    {
        var url = $"{serverUrl.TrimEnd('/')}/api/v1/sync/pull?since={since}&limit={limit}";
        if (!string.IsNullOrEmpty(kind))
        {
            url += $"&kind={Uri.EscapeDataString(kind)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Network error during sync pull.");
            return new PullRecordsResult(PullStatus.NetworkError, ErrorMessage: ex.Message);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new PullRecordsResult(PullStatus.Unauthorized);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return new PullRecordsResult(PullStatus.BadRequest, ErrorMessage: err);
        }

        if ((int)response.StatusCode >= 500)
        {
            return new PullRecordsResult(PullStatus.ServerError, ErrorMessage: $"HTTP {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new PullRecordsResult(PullStatus.ServerError, ErrorMessage: $"HTTP {(int)response.StatusCode}");
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            var pullResp = JsonSerializer.Deserialize<PullResponse>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (pullResp == null || pullResp.Records == null)
            {
                return new PullRecordsResult(PullStatus.BadJson, ErrorMessage: "Missing records in pull response");
            }

            return new PullRecordsResult(PullStatus.Success, Response: pullResp);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse pull response JSON.");
            return new PullRecordsResult(PullStatus.BadJson, ErrorMessage: ex.Message);
        }
    }

    public virtual async Task<HeadMediaResult> HeadMediaAsync(
        string serverUrl,
        string accessToken,
        string sha256Hex,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/media/{sha256Hex.ToLowerInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Network error during HEAD media.");
            throw;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new HeadMediaResult(false, null, Unauthorized: true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new HeadMediaResult(false, null);
        }

        if (response.IsSuccessStatusCode)
        {
            string? nonce = null;
            if (response.Headers.TryGetValues("X-Nonce", out var values))
            {
                nonce = values.FirstOrDefault();
            }
            return new HeadMediaResult(true, nonce);
        }

        return new HeadMediaResult(false, null, Failed: true);
    }

    public virtual async Task<PutMediaResult> PutMediaAsync(
        string serverUrl,
        string accessToken,
        string sha256Hex,
        byte[] wireBlob,
        string nonceBase64,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/media/{sha256Hex.ToLowerInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Nonce", nonceBase64);
        request.Content = new ByteArrayContent(wireBlob);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Network error during PUT media.");
            throw;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new PutMediaResult(MediaUploadStatus.Unauthorized);
        }

        if (response.StatusCode == (HttpStatusCode)413) // PayloadTooLarge
        {
            return new PutMediaResult(MediaUploadStatus.PayloadTooLarge, ErrorMessage: "413 Payload Too Large");
        }

        if (response.StatusCode == (HttpStatusCode)507) // InsufficientStorage
        {
            return new PutMediaResult(MediaUploadStatus.InsufficientStorage, ErrorMessage: "507 Insufficient Storage");
        }

        if (response.IsSuccessStatusCode)
        {
            return new PutMediaResult(MediaUploadStatus.Success, nonceBase64);
        }

        var err = await response.Content.ReadAsStringAsync(ct);
        return new PutMediaResult(MediaUploadStatus.Error, ErrorMessage: $"HTTP {(int)response.StatusCode}: {err}");
    }

    public virtual async Task<PostHealthStatus> PostHealthAsync(
        string serverUrl,
        string token,
        CustomSync.Capture.Maintenance.StorageSnapshot snapshot,
        CancellationToken ct = default)
    {
        var endpoint = $"{serverUrl.TrimEnd('/')}/api/v1/devices/health";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var body = new Dictionary<string, object?>
        {
            ["rss_bytes"] = snapshot.ProcessRssBytes,
            ["memory_limit_bytes"] = snapshot.MemoryLimitBytes,
            ["cache_db_bytes"] = snapshot.CacheDatabaseBytes,
            ["media_store_bytes"] = snapshot.MediaStoreBytes,
            ["media_store_files"] = snapshot.MediaStoreFiles,
            ["tdlib_files_bytes"] = snapshot.TdlibFilesBytes,
            ["tdlib_database_bytes"] = snapshot.TdlibDatabaseBytes,
            ["free_disk_bytes"] = snapshot.FreeDiskBytes
        };

        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Health report HTTP request failed: {ExceptionType}", ex.GetType().Name);
            return PostHealthStatus.Error;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return PostHealthStatus.Unauthorized;
        }

        if (response.IsSuccessStatusCode)
        {
            return PostHealthStatus.Success;
        }

        // Faqat status kodi: URL, token va javob tanasi log'ga tushmaydi.
        // Logsiz doim rad etilayotgan hisobot (masalan, eski server)
        // capture tomonida butunlay ko'rinmas edi.
        _logger?.LogWarning("Health report was rejected: HTTP {StatusCode}", (int)response.StatusCode);
        return PostHealthStatus.Error;
    }
}
