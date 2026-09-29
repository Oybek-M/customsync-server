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
        output ??= Console.Out;
        var rawInput = prompt.Prompt("Enter 64-character hex master key: ", isSecret: true);
        var key = DeviceCredentials.ParseMasterKeyHex(rawInput);
        if (key == null)
        {
            output.WriteLine("Error: Master key must be exactly 64 hexadecimal characters.");
            return 1;
        }

        var path = config["Capture:Sync:MasterKeyPath"] ?? "/var/lib/customsync-capture/master.key";
        try
        {
            DeviceCredentials.SaveMasterKey(path, key);
            output.WriteLine($"Master key successfully saved to {path}.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error saving master key to {path}: {ex.Message}");
            return 1;
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
