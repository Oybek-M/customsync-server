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

        var newRefreshToken = root.GetProperty("refresh_token").GetString()!;
        var newAccessToken = root.GetProperty("access_token").GetString()!;
        var expiresAt = root.GetProperty("expires_at").GetInt64();

        return new RefreshResult(newRefreshToken, newAccessToken, expiresAt);
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
}
