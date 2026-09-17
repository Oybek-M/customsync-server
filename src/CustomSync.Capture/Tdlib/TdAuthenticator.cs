using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Drives the TDLib authorization state machine.
/// Supports both interactive mode (--login) and headless non-interactive mode.
/// </summary>
public class TdAuthenticator
{
    private readonly ITdClient _client;
    private readonly IConfiguration _config;
    private readonly IConsolePrompt _prompt;
    private readonly bool _isInteractive;
    private readonly ILogger<TdAuthenticator>? _logger;

    public bool IsReady { get; private set; }
    public bool IsClosed { get; private set; }

    public TdAuthenticator(
        ITdClient client,
        IConfiguration config,
        IConsolePrompt prompt,
        bool isInteractive,
        ILogger<TdAuthenticator>? logger = null)
    {
        _client = client;
        _config = config;
        _prompt = prompt;
        _isInteractive = isInteractive;
        _logger = logger;
    }

    public async Task<string> ProcessAuthorizationStateAsync(string rawStateJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(rawStateJson);
        var root = doc.RootElement;

        JsonElement stateElement = root;
        if (root.TryGetProperty("authorization_state", out var innerState))
        {
            stateElement = innerState;
        }

        var typeStr = stateElement.TryGetProperty("@type", out var typeProp) ? typeProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(typeStr))
        {
            throw new InvalidOperationException("Authorization state missing @type property.");
        }

        var normalized = NormalizeState(typeStr);

        switch (normalized)
        {
            case "WaitTdlibParameters":
                return await HandleWaitTdlibParametersAsync(ct);

            case "WaitPhoneNumber":
                EnsureInteractive("phone number");
                return await HandleWaitPhoneNumberAsync(ct);

            case "WaitCode":
                EnsureInteractive("code");
                return await HandleWaitCodeAsync(ct);

            case "WaitPassword":
                EnsureInteractive("password");
                return await HandleWaitPasswordAsync(ct);

            case "Ready":
                IsReady = true;
                _logger?.LogInformation("TDLib authorization ready.");
                return "ready";

            case "Closed":
                IsClosed = true;
                _logger?.LogInformation("TDLib authorization closed.");
                return "closed";

            case "WaitRegistration":
                throw new InvalidOperationException("Registration is not supported: this flow never creates a new account.");

            default:
                throw new InvalidOperationException($"Unsupported authorization state: '{typeStr}'. Cannot proceed.");
        }
    }

    private void EnsureInteractive(string step)
    {
        if (!_isInteractive)
        {
            _logger?.LogError("Session is not authorized. Run CustomSync.Capture with --login on the VPS to authenticate.");
            throw new InvalidOperationException($"Session is not authorized ({step} required). Run CustomSync.Capture with --login on the VPS to authenticate.");
        }
    }

    private async Task<string> HandleWaitTdlibParametersAsync(CancellationToken ct)
    {
        var apiId = int.TryParse(_config["Telegram:ApiId"], out var id) ? id : 0;
        var apiHash = _config["Telegram:ApiHash"] ?? "";
        var dbDir = _config["Telegram:DatabaseDirectory"] ?? "/var/lib/customsync-capture/tdlib";
        var filesDir = _config["Telegram:FilesDirectory"] ?? "/var/lib/customsync-capture/files";

        var payload = new JsonObject
        {
            ["@type"] = "setTdlibParameters",
            ["database_directory"] = dbDir,
            ["files_directory"] = filesDir,
            ["api_id"] = apiId,
            ["api_hash"] = apiHash,
            ["system_language_code"] = "en",
            ["device_model"] = "CustomSync Capture",
            ["application_version"] = "1.0",
            ["use_message_database"] = true,
            ["use_file_database"] = true,
            ["use_chat_info_database"] = true,
            ["use_secret_chats"] = false
        }.ToJsonString();

        _logger?.LogInformation("Sending setTdlibParameters to TDLib.");
        return await _client.SendAsync(payload, ct: ct);
    }

    private async Task<string> HandleWaitPhoneNumberAsync(CancellationToken ct)
    {
        var phone = _prompt.Prompt("Enter phone number: ");
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new InvalidOperationException("Phone number cannot be empty.");
        }

        var payload = new JsonObject
        {
            ["@type"] = "setAuthenticationPhoneNumber",
            ["phone_number"] = phone.Trim()
        }.ToJsonString();

        return await _client.SendAsync(payload, ct: ct);
    }

    private async Task<string> HandleWaitCodeAsync(CancellationToken ct)
    {
        var code = _prompt.Prompt("Enter authentication code: ");
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Authentication code cannot be empty.");
        }

        var payload = new JsonObject
        {
            ["@type"] = "checkAuthenticationCode",
            ["code"] = code.Trim()
        }.ToJsonString();

        return await _client.SendAsync(payload, ct: ct);
    }

    private async Task<string> HandleWaitPasswordAsync(CancellationToken ct)
    {
        var password = _prompt.Prompt("Enter 2FA password: ", isSecret: true);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password cannot be empty.");
        }

        var payload = new JsonObject
        {
            ["@type"] = "checkAuthenticationPassword",
            ["password"] = password
        }.ToJsonString();

        return await _client.SendAsync(payload, ct: ct);
    }

    private static string NormalizeState(string type)
    {
        if (type.StartsWith("authorizationState", StringComparison.OrdinalIgnoreCase))
        {
            return type["authorizationState".Length..];
        }
        return type;
    }
}
