using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Sync;

public static class SyncCliCommands
{
    public static int SetKey(
        IConfiguration config,
        IConsolePrompt prompt,
        TextWriter? output = null)
    {
        using var http = new HttpClient();
        return SetKeyAsync(config, prompt, http, output).GetAwaiter().GetResult();
    }

    public static async Task<int> SetKeyAsync(
        IConfiguration config,
        IConsolePrompt prompt,
        HttpClient httpClient,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        output ??= Console.Out;

        // 1. Check enrollment (state file)
        var statePath = config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json";
        var deviceState = DeviceCredentials.LoadDeviceState(statePath);
        if (deviceState == null)
        {
            output.WriteLine($"Error: Device is not enrolled. State file not found at {statePath}. Run --enroll first.");
            return 1;
        }

        var serverUrl = config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            output.WriteLine("Error: Capture:Sync:ServerUrl is not configured.");
            return 1;
        }

        // 2. Refresh token and persist rotated token
        var syncClient = new CaptureSyncHttpClient(httpClient);
        RefreshResult? refreshResult;
        try
        {
            refreshResult = await syncClient.RefreshAndPersistTokenAsync(serverUrl, statePath, deviceState, ct);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error refreshing token: {ex.Message}");
            return 1;
        }

        if (refreshResult == null || string.IsNullOrEmpty(refreshResult.AccessToken))
        {
            output.WriteLine("Error: Device authorization revoked or refresh token invalid (HTTP 401). Run --enroll again.");
            return 1;
        }

        string accessToken = refreshResult.AccessToken;

        // 3. List wraps and keep wrap_type == "passphrase"
        IReadOnlyList<KeyWrapSummary> wraps;
        try
        {
            wraps = await syncClient.ListKeyWrapsAsync(serverUrl, accessToken, ct);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error fetching key wraps: {ex.Message}");
            return 1;
        }

        var passphraseWraps = wraps
            .Where(w => string.Equals(w.WrapType, "passphrase", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (passphraseWraps.Count == 0)
        {
            output.WriteLine("No passphrase wraps found on the server. Please create one in tdesktop (Sync tab -> archive password).");
            return 1;
        }

        string selectedWrapId;
        if (passphraseWraps.Count == 1)
        {
            selectedWrapId = passphraseWraps[0].WrapId;
        }
        else
        {
            output.WriteLine("Available passphrase wraps:");
            for (int i = 0; i < passphraseWraps.Count; i++)
            {
                output.WriteLine($"  {i + 1}. Label: \"{passphraseWraps[i].Label}\", Created: {passphraseWraps[i].CreatedAt}");
            }

            var selection = prompt.Prompt($"Select wrap [1-{passphraseWraps.Count}]: ", isSecret: false)?.Trim();
            if (int.TryParse(selection, out var idx) && idx >= 1 && idx <= passphraseWraps.Count)
            {
                selectedWrapId = passphraseWraps[idx - 1].WrapId;
            }
            else
            {
                var matched = passphraseWraps.FirstOrDefault(w =>
                    string.Equals(w.WrapId, selection, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(w.Label, selection, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    selectedWrapId = matched.WrapId;
                }
                else
                {
                    output.WriteLine("Invalid wrap selection.");
                    return 1;
                }
            }
        }

        // 4. GET /api/v1/keys/wraps/{wrap_id} exactly once
        var wrapResp = await syncClient.GetKeyWrapAsync(serverUrl, accessToken, selectedWrapId, ct);
        if (wrapResp.Status == GetWrapStatus.RateLimited)
        {
            output.WriteLine("Hourly rate limit reached for key wraps (maximum 5 requests per hour). Please try again later.");
            return 1;
        }

        if (wrapResp.Status != GetWrapStatus.Success || wrapResp.Wrap == null)
        {
            output.WriteLine($"Failed to retrieve key wrap: {wrapResp.ErrorMessage}");
            return 1;
        }

        var wrap = wrapResp.Wrap;
        byte[] salt;
        byte[] nonce;
        byte[] wrappedKey;
        try
        {
            salt = Convert.FromBase64String(wrap.Salt);
            nonce = Convert.FromBase64String(wrap.Nonce);
            wrappedKey = Convert.FromBase64String(wrap.WrappedKey);
        }
        catch
        {
            output.WriteLine("Invalid base64 encoding in key wrap returned by server.");
            return 1;
        }

        int maxIterations = 10_000_000;
        if (int.TryParse(config["Capture:Sync:MaxWrapIterations"], out var configuredMax) && configuredMax > 0)
        {
            maxIterations = configuredMax;
        }

        if (wrap.Iterations < 1 || wrap.Iterations > maxIterations ||
            salt.Length != 16 || nonce.Length != 12 || wrappedKey.Length != 48)
        {
            output.WriteLine("Invalid or malformed wrap returned by server.");
            return 1;
        }

        // 5. Ask for passphrase (at most 3 attempts, all against wrap in memory)
        byte[]? masterKey = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var passphrase = prompt.Prompt("Enter passphrase: ", isSecret: true);
            if (string.IsNullOrEmpty(passphrase))
            {
                output.WriteLine("Passphrase cannot be empty.");
                continue;
            }

            var unwrapResult = SyncCrypto.UnwrapMasterKey(passphrase, salt, wrap.Iterations, nonce, wrappedKey, maxIterations);
            if (unwrapResult.Status == KeyUnwrapStatus.Success && unwrapResult.MasterKey != null)
            {
                masterKey = unwrapResult.MasterKey;
                break;
            }

            if (unwrapResult.Status == KeyUnwrapStatus.WrongPassphrase)
            {
                if (attempt < 3)
                {
                    output.WriteLine($"Incorrect passphrase. Please try again (attempt {attempt}/3).");
                }
                else
                {
                    output.WriteLine("Incorrect passphrase. Maximum attempts (3) exceeded.");
                }
            }
            else
            {
                output.WriteLine("Invalid or malformed wrap.");
                return 1;
            }
        }

        if (masterKey == null)
        {
            return 1;
        }

        // 6. Print FP and confirm
        string fp = SyncCrypto.Fingerprint(masterKey);
        output.WriteLine($"Key fingerprint (FP): {fp}");
        var confirmation = prompt.Prompt("Does this match 'Kalit barmoq izi (FP)' in tdesktop Sync tab? [y/N]: ", isSecret: false)?.Trim();
        if (!string.Equals(confirmation, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Confirmation rejected. Master key was not saved.");
            return 1;
        }

        // 7. Existing key file check
        var keyPath = config["Capture:Sync:MasterKeyPath"] ?? "/var/lib/customsync-capture/master.key";
        if (File.Exists(keyPath))
        {
            var existingKey = DeviceCredentials.LoadMasterKey(keyPath, logger: null);
            if (existingKey != null)
            {
                string existingFp = SyncCrypto.Fingerprint(existingKey);
                if (string.Equals(existingFp, fp, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"The master key file already exists with the same fingerprint ({fp}). Nothing to update.");
                    return 0;
                }
                else
                {
                    output.WriteLine($"Warning: An existing master key exists at {keyPath} with a different fingerprint ({existingFp}). It will be replaced.");
                    var replaceConfirm = prompt.Prompt("Are you sure you want to replace the existing key? [y/N]: ", isSecret: false)?.Trim();
                    if (!string.Equals(replaceConfirm, "y", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(replaceConfirm, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        output.WriteLine("Replacement cancelled. Master key was not saved.");
                        return 1;
                    }
                }
            }
            else
            {
                // O'qib bo'lmaydigan fayl (buzilgan yoki ruxsati noto'g'ri)
                // ham egasining yagona nusxasi bo'lishi mumkin — so'ramasdan
                // ustiga yozilmaydi.
                output.WriteLine($"Warning: {keyPath} exists but cannot be read as a master key (bad content or permissions). It will be replaced.");
                var replaceConfirm = prompt.Prompt("Are you sure you want to replace the existing file? [y/N]: ", isSecret: false)?.Trim();
                if (!string.Equals(replaceConfirm, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(replaceConfirm, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine("Replacement cancelled. Master key was not saved.");
                    return 1;
                }
            }
        }

        // 8. Save master key
        try
        {
            DeviceCredentials.SaveMasterKey(keyPath, masterKey);
            output.WriteLine($"Master key successfully saved to {keyPath}.");
            output.WriteLine($"Key fingerprint (FP): {fp}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error saving master key to {keyPath}: {ex.Message}");
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public static async Task<int> EnrollAsync(
        IConfiguration config,
        IConsolePrompt prompt,
        HttpClient httpClient,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        output ??= Console.Out;
        var code = prompt.Prompt("Enrollment code: ", isSecret: true)?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            output.WriteLine("Error: Enrollment code is required.");
            return 1;
        }

        var deviceName = prompt.Prompt("Device name [capture-vps]: ", isSecret: false)?.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = "capture-vps";
        }

        var serverUrl = config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            output.WriteLine("Error: Capture:Sync:ServerUrl is not configured.");
            return 1;
        }

        var statePath = config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json";

        var enrollEndpoint = $"{serverUrl.TrimEnd('/')}/api/v1/devices/enroll";
        var requestBody = JsonSerializer.Serialize(new
        {
            code,
            name = deviceName,
            platform = "service"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, enrollEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(ct);
                output.WriteLine($"Enrollment failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {errContent}");
                return 1;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var deviceId = root.GetProperty("device_id").GetString();
            var refreshToken = root.GetProperty("refresh_token").GetString();

            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(refreshToken))
            {
                output.WriteLine("Error: Enrollment response missing device_id or refresh_token.");
                return 1;
            }

            DeviceCredentials.SaveDeviceState(statePath, new DeviceState(deviceId, refreshToken));
            output.WriteLine($"Device enrolled successfully. Device ID: {deviceId}. State saved to {statePath}.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error during enrollment: {ex.Message}");
            return 1;
        }
    }
}
