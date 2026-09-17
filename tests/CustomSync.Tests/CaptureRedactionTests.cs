using System.Text.Json.Nodes;
using CustomSync.Capture.Tdlib;
using Xunit;

namespace CustomSync.Tests;

public class CaptureRedactionTests
{
    [Fact]
    public void Test12_Payload_containing_credentials_redacts_values_and_preserves_keys()
    {
        var rawPayload = new JsonObject
        {
            ["@type"] = "setAuthenticationParameters",
            ["api_hash"] = "super_secret_api_hash_123",
            ["phone_number"] = "+998901234567",
            ["code"] = "98765",
            ["password"] = "my_2fa_secret_password",
            ["recovery_code"] = "rec_code_111",
            ["email_address"] = "user@example.uz",
            ["authentication_code"] = "auth_code_222"
        }.ToJsonString();

        var redacted = TdRedactor.Redact(rawPayload);

        // Assert none of the secret values appear anywhere in the redacted output
        Assert.DoesNotContain("super_secret_api_hash_123", redacted);
        Assert.DoesNotContain("+998901234567", redacted);
        Assert.DoesNotContain("98765", redacted);
        Assert.DoesNotContain("my_2fa_secret_password", redacted);
        Assert.DoesNotContain("rec_code_111", redacted);
        Assert.DoesNotContain("user@example.uz", redacted);
        Assert.DoesNotContain("auth_code_222", redacted);

        // Assert all keys are still preserved
        var parsed = JsonNode.Parse(redacted)!;
        Assert.Equal(TdRedactor.RedactedValue, parsed["api_hash"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["phone_number"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["code"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["password"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["recovery_code"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["email_address"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, parsed["authentication_code"]?.ToString());
    }

    [Fact]
    public void Test13_Redaction_survives_nested_objects_and_does_not_corrupt_other_fields()
    {
        var rawPayload = new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["chat_id"] = 123456789,
            ["title"] = "Toshkent Developers",
            ["authorization_state"] = new JsonObject
            {
                ["@type"] = "authorizationStateWaitCode",
                ["code_info"] = new JsonObject
                {
                    ["phone_number"] = "+998991234567",
                    ["code"] = "43210"
                }
            },
            ["safe_array"] = new JsonArray
            {
                "item1",
                new JsonObject
                {
                    ["password"] = "nested_pwd",
                    ["label"] = "safe_label"
                }
            }
        }.ToJsonString();

        var redacted = TdRedactor.Redact(rawPayload);

        Assert.DoesNotContain("+998991234567", redacted);
        Assert.DoesNotContain("43210", redacted);
        Assert.DoesNotContain("nested_pwd", redacted);

        var parsed = JsonNode.Parse(redacted)!;
        Assert.Equal(123456789, (long)parsed["chat_id"]!);
        Assert.Equal("Toshkent Developers", parsed["title"]?.ToString());

        var codeInfo = parsed["authorization_state"]?["code_info"];
        Assert.NotNull(codeInfo);
        Assert.Equal(TdRedactor.RedactedValue, codeInfo["phone_number"]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, codeInfo["code"]?.ToString());

        var arr = parsed["safe_array"]?.AsArray();
        Assert.NotNull(arr);
        Assert.Equal("item1", arr[0]?.ToString());
        Assert.Equal(TdRedactor.RedactedValue, arr[1]?["password"]?.ToString());
        Assert.Equal("safe_label", arr[1]?["label"]?.ToString());
    }
}
