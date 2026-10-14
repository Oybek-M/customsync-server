using System.Text.Json.Nodes;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CustomSync.Tests;

public class FakeConsolePrompt : IConsolePrompt
{
    public Queue<string> Inputs { get; } = new();
    public List<(string Message, bool IsSecret)> Prompts { get; } = new();
    public int PromptCallCount => Prompts.Count;

    public string? Prompt(string message, bool isSecret = false)
    {
        Prompts.Add((message, isSecret));
        return Inputs.Count > 0 ? Inputs.Dequeue() : null;
    }
}

public class CaptureAuthTests
{
    private static IConfiguration CreateConfig(int apiId = 12345, string apiHash = "fake_hash")
    {
        var tempKey = Path.Combine(Path.GetTempPath(), "cs-auth-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempKey, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = apiId.ToString(),
                ["Telegram:ApiHash"] = apiHash,
                ["Telegram:DatabaseDirectory"] = "/tmp/tdlib_db",
                ["Telegram:FilesDirectory"] = "/tmp/tdlib_files",
                ["Telegram:DatabaseEncryptionKeyFile"] = tempKey
            })
            .Build();
    }

    // errorFor: the TDLib error returned for a request type (null means "ok").
    private static (FakeTdTransport Transport, TdClient Client, FakeConsolePrompt Prompt, TdAuthenticator Auth) CreateAuthenticator(
        bool isInteractive = true, IConfiguration? config = null, Func<string, (int Code, string Message)?>? errorFor = null)
    {
        var transport = new FakeTdTransport();
        transport.OnSend = (clientId, reqJson) =>
        {
            var node = JsonNode.Parse(reqJson)!;
            var extra = node["@extra"]?.ToString();
            if (extra != null)
            {
                var error = errorFor?.Invoke(node["@type"]!.ToString());
                var reply = error is { } e
                    ? new JsonObject { ["@type"] = "error", ["code"] = e.Code, ["message"] = e.Message, ["@extra"] = extra }
                    : new JsonObject { ["@type"] = "ok", ["@extra"] = extra };
                transport.EnqueueIncoming(reply.ToJsonString());
            }
        };

        var client = new TdClient(transport);
        var prompt = new FakeConsolePrompt();
        var cfg = config ?? CreateConfig();
        var auth = new TdAuthenticator(client, cfg, prompt, isInteractive);

        return (transport, client, prompt, auth);
    }

    [Fact]
    public async Task Test6_Each_state_produces_expected_outgoing_request()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator(isInteractive: true);
        using (client)
        {
            prompt.Inputs.Enqueue("+998901234567");
            prompt.Inputs.Enqueue("54321");
            prompt.Inputs.Enqueue("mypassword");

            // 1. WaitTdlibParameters -> setTdlibParameters
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitTdlibParameters\"}");
            var lastReq1 = transport.OutgoingRequests.Last();
            var node1 = JsonNode.Parse(lastReq1.RequestJson)!;
            Assert.Equal("setTdlibParameters", node1["@type"]?.ToString());

            // 2. WaitPhoneNumber -> setAuthenticationPhoneNumber
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPhoneNumber\"}");
            var lastReq2 = transport.OutgoingRequests.Last();
            var node2 = JsonNode.Parse(lastReq2.RequestJson)!;
            Assert.Equal("setAuthenticationPhoneNumber", node2["@type"]?.ToString());
            Assert.Equal("+998901234567", node2["phone_number"]?.ToString());

            // 3. WaitCode -> checkAuthenticationCode
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitCode\"}");
            var lastReq3 = transport.OutgoingRequests.Last();
            var node3 = JsonNode.Parse(lastReq3.RequestJson)!;
            Assert.Equal("checkAuthenticationCode", node3["@type"]?.ToString());
            Assert.Equal("54321", node3["code"]?.ToString());

            // 4. WaitPassword -> checkAuthenticationPassword
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPassword\"}");
            var lastReq4 = transport.OutgoingRequests.Last();
            var node4 = JsonNode.Parse(lastReq4.RequestJson)!;
            Assert.Equal("checkAuthenticationPassword", node4["@type"]?.ToString());
            Assert.Equal("mypassword", node4["password"]?.ToString());

            // 5. Ready -> is ready
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateReady\"}");
            Assert.True(auth.IsReady);
        }
    }

    [Fact]
    public async Task Test7_WaitRegistration_refuses_with_clear_message()
    {
        var (_, client, _, auth) = CreateAuthenticator();
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitRegistration\"}");
            });

            Assert.Contains("never creates a new account", ex.Message);
        }
    }

    [Fact]
    public async Task Test8_Unknown_or_new_state_refuses_and_names_state()
    {
        var (_, client, _, auth) = CreateAuthenticator();
        using (client)
        {
            // A real state this flow cannot satisfy (Telegram demands a Premium purchase).
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPremiumPurchase\"}");
            });

            Assert.Contains("authorizationStateWaitPremiumPurchase", ex.Message);
        }
    }

    [Fact]
    public async Task Test9_SetTdlibParameters_carries_the_four_flags()
    {
        var (transport, client, _, auth) = CreateAuthenticator();
        using (client)
        {
            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitTdlibParameters\"}");

            var sent = transport.OutgoingRequests.Last();
            var node = JsonNode.Parse(sent.RequestJson)!;

            Assert.True((bool)node["use_message_database"]!);
            Assert.True((bool)node["use_file_database"]!);
            Assert.True((bool)node["use_chat_info_database"]!);
            Assert.False((bool)node["use_secret_chats"]!);
        }
    }

    [Fact]
    public async Task Test10_Non_interactive_start_with_unauthorized_session_does_not_prompt_and_names_login()
    {
        var (_, client, prompt, auth) = CreateAuthenticator(isInteractive: false);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPhoneNumber\"}");
            });

            Assert.Equal(0, prompt.PromptCallCount);
            Assert.Contains("--login", ex.Message);
        }
    }

    [Fact]
    public async Task Test11_Closed_stops_without_looping()
    {
        var (transport, client, _, auth) = CreateAuthenticator();
        using (client)
        {
            var initialReqCount = transport.OutgoingRequests.Count;
            var res = await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateClosed\"}");

            Assert.Equal("closed", res);
            Assert.True(auth.IsClosed);
            Assert.Equal(initialReqCount, transport.OutgoingRequests.Count);
        }
    }

    // A mistyped code must not end the whole login: starting over requests a new
    // code, and repeated requests make Telegram block code delivery.
    [Fact]
    public async Task Test12_Rejected_code_is_asked_again_with_the_reason()
    {
        var rejections = 1;
        var (transport, client, prompt, auth) = CreateAuthenticator(
            errorFor: type => type == "checkAuthenticationCode" && rejections-- > 0 ? (400, "PHONE_CODE_INVALID") : null);
        using (client)
        {
            prompt.Inputs.Enqueue("11111");
            prompt.Inputs.Enqueue("54321");

            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitCode\"}");

            var codes = SentOfType(transport, "checkAuthenticationCode").Select(n => n["code"]!.ToString());
            Assert.Equal(new[] { "11111", "54321" }, codes);
            Assert.Equal(2, prompt.PromptCallCount);
            Assert.Contains("PHONE_CODE_INVALID", prompt.Prompts[1].Message);
        }
    }

    [Fact]
    public async Task Test13_Rejected_password_stops_after_three_attempts_and_names_the_reason()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator(
            errorFor: type => type == "checkAuthenticationPassword" ? (400, "PASSWORD_HASH_INVALID") : null);
        using (client)
        {
            foreach (var input in new[] { "p1", "p2", "p3", "p4" }) prompt.Inputs.Enqueue(input);

            var ex = await Assert.ThrowsAsync<TdException>(() =>
                auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPassword\"}"));

            Assert.Contains("PASSWORD_HASH_INVALID", ex.Message);
            Assert.Equal(3, SentOfType(transport, "checkAuthenticationPassword").Count);
            Assert.Equal(3, prompt.PromptCallCount);
            Assert.All(prompt.Prompts, p => Assert.True(p.IsSecret));
        }
    }

    // Errors that retyping cannot fix are not retried: under FLOOD each attempt may
    // extend the block, and an expired code is dead.
    [Theory]
    [InlineData(429, "Too Many Requests: retry after 3600")]
    [InlineData(400, "PHONE_CODE_EXPIRED")]
    [InlineData(400, "PHONE_NUMBER_FLOOD")]
    [InlineData(401, "AUTH_KEY_INVALID")]
    public async Task Test14_Errors_retyping_cannot_fix_are_not_retried(int code, string message)
    {
        var (transport, client, prompt, auth) = CreateAuthenticator(
            errorFor: type => type == "checkAuthenticationCode" ? (code, message) : null);
        using (client)
        {
            prompt.Inputs.Enqueue("11111");
            prompt.Inputs.Enqueue("54321");

            var ex = await Assert.ThrowsAsync<TdException>(() =>
                auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitCode\"}"));

            Assert.Equal(code, ex.Code);
            Assert.Single(SentOfType(transport, "checkAuthenticationCode"));
            Assert.Equal(1, prompt.PromptCallCount);
        }
    }

    [Fact]
    public async Task Test15_Empty_input_is_asked_again_without_contacting_Telegram()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator();
        using (client)
        {
            prompt.Inputs.Enqueue("  ");
            prompt.Inputs.Enqueue("54321");

            await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitCode\"}");

            var codes = SentOfType(transport, "checkAuthenticationCode").Select(n => n["code"]!.ToString());
            Assert.Equal(new[] { "54321" }, codes);
            Assert.Equal(2, prompt.PromptCallCount);
        }
    }

    [Fact]
    public async Task Test16_Repeated_empty_input_stops_after_three_attempts()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator();
        using (client)
        {
            foreach (var input in new[] { "", " ", "\t", "54321" }) prompt.Inputs.Enqueue(input);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitCode\"}"));

            Assert.Contains("cannot be empty", ex.Message);
            Assert.Equal(3, prompt.PromptCallCount);
            Assert.Empty(SentOfType(transport, "checkAuthenticationCode"));
        }
    }

    // Closed input (stdin EOF) stops at once instead of asking forever.
    [Fact]
    public async Task Test17_Closed_input_stops_at_once()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator();
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitPhoneNumber\"}"));

            Assert.Contains("cannot be empty", ex.Message);
            Assert.Equal(1, prompt.PromptCallCount);
            Assert.Empty(SentOfType(transport, "setAuthenticationPhoneNumber"));
        }
    }

    [Fact]
    public async Task Test18_Email_address_state_sends_setAuthenticationEmailAddress()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator();
        using (client)
        {
            prompt.Inputs.Enqueue(" me@example.com ");

            await auth.ProcessAuthorizationStateAsync(
                "{\"@type\":\"authorizationStateWaitEmailAddress\",\"allow_apple_id\":false,\"allow_google_id\":false}");

            var sent = Assert.Single(SentOfType(transport, "setAuthenticationEmailAddress"));
            Assert.Equal("me@example.com", sent["email_address"]!.ToString());
            Assert.False(prompt.Prompts[0].IsSecret);
        }
    }

    [Fact]
    public async Task Test19_Email_code_state_sends_checkAuthenticationEmailCode_and_shows_where_the_code_went()
    {
        var (transport, client, prompt, auth) = CreateAuthenticator();
        using (client)
        {
            prompt.Inputs.Enqueue("123456");

            await auth.ProcessAuthorizationStateAsync(
                "{\"@type\":\"authorizationStateWaitEmailCode\",\"allow_apple_id\":false,\"allow_google_id\":false," +
                "\"code_info\":{\"@type\":\"emailAddressAuthenticationCodeInfo\",\"email_address_pattern\":\"m***@example.com\",\"length\":6}}");

            var sent = Assert.Single(SentOfType(transport, "checkAuthenticationEmailCode"));
            Assert.Equal("emailAddressAuthenticationCode", sent["code"]!["@type"]!.ToString());
            Assert.Equal("123456", sent["code"]!["code"]!.ToString());
            Assert.Contains("m***@example.com", prompt.Prompts[0].Message);
        }
    }

    [Theory]
    [InlineData("authorizationStateWaitEmailAddress")]
    [InlineData("authorizationStateWaitEmailCode")]
    public async Task Test20_Email_states_without_login_do_not_prompt_and_name_login(string state)
    {
        var (_, client, prompt, auth) = CreateAuthenticator(isInteractive: false);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                auth.ProcessAuthorizationStateAsync($"{{\"@type\":\"{state}\"}}"));

            Assert.Equal(0, prompt.PromptCallCount);
            Assert.Contains("--login", ex.Message);
        }
    }

    // Real TDLib (td_json_client.h): an instance created by td_create_client_id sends no
    // updates until it receives its first request, and answers requests made before
    // setTdlibParameters with an error. Fakes that push updates on their own hid this:
    // against the real library the gate waited for a state that never came.
    [Fact]
    public async Task Test21_Gate_wakes_a_TDLib_instance_that_stays_silent_until_its_first_request()
    {
        var transport = new FakeTdTransport();
        var started = 0;
        var initialized = false;
        transport.OnSend = (clientId, reqJson) =>
        {
            var node = JsonNode.Parse(reqJson)!;
            var type = node["@type"]!.ToString();
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                transport.EnqueueIncoming(AuthState("authorizationStateWaitTdlibParameters"));
            }

            if (type == "setTdlibParameters")
            {
                initialized = true;
            }
            var reply = initialized
                ? new JsonObject { ["@type"] = "ok" }
                : new JsonObject
                {
                    ["@type"] = "error",
                    ["code"] = 400,
                    ["message"] = "Initialization parameters are needed: call setTdlibParameters first"
                };
            reply["@extra"] = node["@extra"]?.ToString();
            transport.EnqueueIncoming(reply.ToJsonString());

            if (type == "setTdlibParameters")
            {
                transport.EnqueueIncoming(AuthState("authorizationStateReady"));
            }
        };

        using var client = new TdClient(transport);
        var auth = new TdAuthenticator(client, CreateConfig(), new FakeConsolePrompt(), isInteractive: false);

        var outcome = await new AuthorizationGate(client, auth).RunAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.Ready, outcome.Message);
    }

    private static string AuthState(string state) =>
        $"{{\"@type\":\"updateAuthorizationState\",\"authorization_state\":{{\"@type\":\"{state}\"}}}}";

    private static List<JsonNode> SentOfType(FakeTdTransport transport, string type) =>
        transport.OutgoingRequests
            .Select(r => JsonNode.Parse(r.RequestJson)!)
            .Where(n => n["@type"]?.ToString() == type)
            .ToList();
}
