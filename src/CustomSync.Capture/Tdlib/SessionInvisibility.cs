using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

public record SessionInvisibilityResult(bool Success, string? Error = null);

public static class SessionInvisibility
{
    public const int DefaultTimeoutSeconds = 30;

    public static async Task<SessionInvisibilityResult> EnsureAsync(
        ITdClient client,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        try
        {
            // 1. Send setOption (online = false)
            var setOptionPayload = new JsonObject
            {
                ["@type"] = "setOption",
                ["name"] = "online",
                ["value"] = new JsonObject
                {
                    ["@type"] = "optionValueBoolean",
                    ["value"] = false
                }
            }.ToJsonString();

            var setResponseRaw = await client.SendAsync(setOptionPayload, timeout, linkedCts.Token);
            using (var setDoc = JsonDocument.Parse(setResponseRaw))
            {
                var root = setDoc.RootElement;
                var type = root.TryGetProperty("@type", out var typeProp) ? typeProp.GetString() : null;
                if (type == "error")
                {
                    var msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown error";
                    return new SessionInvisibilityResult(false, $"TDLib error setting online option: {msg}");
                }
                if (type != "ok")
                {
                    return new SessionInvisibilityResult(false, $"Unexpected response for setOption: '{type}'. Expected 'ok'.");
                }
            }

            // 2. Send getOption (online) to verify
            var getOptionPayload = new JsonObject
            {
                ["@type"] = "getOption",
                ["name"] = "online"
            }.ToJsonString();

            var getResponseRaw = await client.SendAsync(getOptionPayload, timeout, linkedCts.Token);
            using (var getDoc = JsonDocument.Parse(getResponseRaw))
            {
                var root = getDoc.RootElement;
                var type = root.TryGetProperty("@type", out var typeProp) ? typeProp.GetString() : null;
                if (type == "error")
                {
                    var msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown error";
                    return new SessionInvisibilityResult(false, $"TDLib error getting online option: {msg}");
                }
                if (type != "optionValueBoolean")
                {
                    return new SessionInvisibilityResult(false, $"Expected optionValueBoolean for 'online', got: '{type}'.");
                }
                if (!root.TryGetProperty("value", out var valProp) || valProp.ValueKind != JsonValueKind.False)
                {
                    return new SessionInvisibilityResult(false, "Option 'online' is not false.");
                }
            }

            return new SessionInvisibilityResult(true);
        }
        catch (TdException ex)
        {
            return new SessionInvisibilityResult(false, $"TDLib error: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SessionInvisibilityResult(false, $"Timed out waiting for session invisibility confirmation after {timeout.TotalSeconds}s.");
        }
        catch (TimeoutException ex)
        {
            return new SessionInvisibilityResult(false, $"Timed out waiting for session invisibility: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SessionInvisibilityResult(false, ex.Message);
        }
    }

    public static TimeSpan ReadTimeout(IConfiguration? config, ILogger? logger = null)
    {
        var raw = config?["Capture:SessionInvisibilityTimeoutSeconds"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        }
        if (int.TryParse(raw, CultureInfo.InvariantCulture, out var sec) && sec > 0)
        {
            return TimeSpan.FromSeconds(sec);
        }
        logger?.LogWarning("Invalid Capture:SessionInvisibilityTimeoutSeconds '{Raw}'; falling back to default {Default}s.", raw, DefaultTimeoutSeconds);
        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }
}
