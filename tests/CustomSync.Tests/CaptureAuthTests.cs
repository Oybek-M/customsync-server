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
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = apiId.ToString(),
                ["Telegram:ApiHash"] = apiHash,
                ["Telegram:DatabaseDirectory"] = "/tmp/tdlib_db",
                ["Telegram:FilesDirectory"] = "/tmp/tdlib_files"
            })
            .Build();
    }

    private static (FakeTdTransport Transport, TdClient Client, FakeConsolePrompt Prompt, TdAuthenticator Auth) CreateAuthenticator(
        bool isInteractive = true, IConfiguration? config = null)
    {
        var transport = new FakeTdTransport();
        transport.OnSend = (clientId, reqJson) =>
        {
            var node = JsonNode.Parse(reqJson)!;
            var extra = node["@extra"]?.ToString();
            if (extra != null)
            {
                var okReply = new JsonObject
                {
                    ["@type"] = "ok",
                    ["@extra"] = extra
                }.ToJsonString();
                transport.EnqueueIncoming(okReply);
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
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await auth.ProcessAuthorizationStateAsync("{\"@type\":\"authorizationStateWaitEmailCode\"}");
            });

            Assert.Contains("authorizationStateWaitEmailCode", ex.Message);
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
}
