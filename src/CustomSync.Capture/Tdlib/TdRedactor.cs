using System.Text.Json;
using System.Text.Json.Nodes;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Redacts sensitive credentials (api_hash, phone_number, code, password, etc.)
/// from TDLib JSON payloads while preserving structure and keys for debugging.
/// </summary>
public static class TdRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_hash",
        "phone_number",
        "code",
        "password",
        "recovery_code",
        "email_address",
        "authentication_code"
    };

    public const string RedactedValue = "[REDACTED]";

    public static string Redact(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json ?? string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return json;

            RedactNode(node);
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            // If not valid JSON, return as-is
            return json;
        }
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var propertyNames = obj.Select(kvp => kvp.Key).ToList();
            foreach (var propName in propertyNames)
            {
                if (SensitiveKeys.Contains(propName))
                {
                    obj[propName] = RedactedValue;
                }
                else if (obj[propName] is JsonNode childNode)
                {
                    RedactNode(childNode);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonNode itemNode)
                {
                    RedactNode(itemNode);
                }
            }
        }
    }
}
