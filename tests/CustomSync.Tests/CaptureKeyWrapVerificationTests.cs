using System.Net;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using CustomSync.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Task 6a-2 tekshiruvida topilgan bo'shliqlar. Soxta server javoblari
/// serverning o'z turlaridan quriladi: ro'yxat — haqiqiy `WrapSummary`,
/// bitta o'ram — `KeyEndpoints` dagi anonim obyekt, ikkalasi snake_case.
/// </summary>
public class CaptureKeyWrapVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"keywrap_verify_{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_dir, "device-state.json");
    private string KeyPath => Path.Combine(_dir, "master.key");

    public CaptureKeyWrapVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        DeviceCredentials.SaveDeviceState(StatePath, new DeviceState("dev-1", "rt-1"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly JsonSerializerOptions ServerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonElement Case(int i) => TestVectors.Get("key_wrap").GetProperty("cases")[i];

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, ServerJson), Encoding.UTF8, "application/json")
    };

    private sealed class Server(string wrapId, JsonElement wrapCase) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/devices/refresh"))
                return Task.FromResult(Json(new { refreshToken = "rt-2", accessToken = "at", expiresAt = DateTime.UtcNow.AddHours(1) }));
            if (path == "/api/v1/keys/wraps")
                return Task.FromResult(Json(new[] { new WrapSummary(wrapId, "passphrase", "tdesktop", DateTime.UtcNow, null) }));
            if (path.StartsWith("/api/v1/keys/wraps/"))
                return Task.FromResult(Json(new
                {
                    WrapId = wrapId,
                    WrapType = "passphrase",
                    Label = "tdesktop",
                    Salt = wrapCase.GetProperty("salt").GetString(),
                    Nonce = wrapCase.GetProperty("nonce").GetString(),
                    WrappedKey = wrapCase.GetProperty("wrapped_key").GetString(),
                    Iterations = wrapCase.GetProperty("iterations").GetInt32(),
                }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ScriptedPrompt(params string[] answers) : IConsolePrompt
    {
        private readonly Queue<string> _answers = new(answers);
        public int SecretPrompts;

        public string? Prompt(string message, bool isSecret = false)
        {
            if (isSecret) SecretPrompts++;
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }

    private IConfiguration Config(string? maxIterations = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "https://sync.example",
            ["Capture:Sync:StatePath"] = StatePath,
            ["Capture:Sync:MasterKeyPath"] = KeyPath,
            ["Capture:Sync:MaxWrapIterations"] = maxIterations,
        }).Build();

    private async Task<(int Exit, string Output)> Run(Server server, ScriptedPrompt prompt, IConfiguration? config = null)
    {
        var output = new StringWriter();
        int exit = await SyncCliCommands.SetKeyAsync(config ?? Config(), prompt, new HttpClient(server), output);
        return (exit, output.ToString());
    }

    [Fact]
    public async Task K01_An_existing_key_file_that_cannot_be_read_is_not_replaced_without_asking()
    {
        // Buzilgan yoki ruxsati noto'g'ri fayl ham egasining kaliti bo'lishi
        // mumkin — uni so'ramasdan ustiga yozish tiklab bo'lmas yo'qotish.
        File.WriteAllText(KeyPath, "not-a-key");
        var c = Case(1);

        var (exit, _) = await Run(new Server("w1", c), new ScriptedPrompt(c.GetProperty("passphrase").GetString()!, "y", "n"));

        Assert.Equal(1, exit);
        Assert.Equal("not-a-key", File.ReadAllText(KeyPath));
    }

    [Fact]
    public async Task K02_Exactly_three_passphrase_attempts_are_allowed()
    {
        var c = Case(1);
        var bad = c.GetProperty("wrong_passphrase").GetString()!;
        var prompt = new ScriptedPrompt(bad, bad, bad, c.GetProperty("passphrase").GetString()!, "y");

        var (exit, _) = await Run(new Server("w1", c), prompt);

        Assert.Equal(1, exit);
        Assert.Equal(3, prompt.SecretPrompts);
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public async Task K03_The_same_key_already_on_disk_is_left_untouched()
    {
        var c = Case(1);
        var upper = c.GetProperty("master_hex").GetString()!.ToUpperInvariant();
        File.WriteAllText(KeyPath, upper);

        var (exit, _) = await Run(new Server("w1", c), new ScriptedPrompt(c.GetProperty("passphrase").GetString()!, "y"));

        Assert.Equal(0, exit);
        Assert.Equal(upper, File.ReadAllText(KeyPath));
    }

    [Fact]
    public async Task K04_The_configured_iteration_ceiling_is_applied_before_asking_for_the_passphrase()
    {
        var c = Case(1); // 1 000 iterations
        var prompt = new ScriptedPrompt(c.GetProperty("passphrase").GetString()!, "y");

        var (exit, _) = await Run(new Server("w1", c), prompt, Config(maxIterations: "500"));

        Assert.Equal(1, exit);
        Assert.Equal(0, prompt.SecretPrompts);
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void K05_Preflight_rejects_a_bad_MaxWrapIterations()
    {
        foreach (var bad in new[] { "abc", "0", "-5" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "hash",
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath(),
                ["Capture:Sync:MaxWrapIterations"] = bad,
            }).Build();

            var report = CapturePreflight.Check(config, _ => true);

            Assert.Contains(report.Errors, e => e.Contains("Capture:Sync:MaxWrapIterations"));
        }
    }

    [Fact]
    public async Task K06_A_wrap_id_from_the_server_cannot_change_the_request_path()
    {
        // Server ishonchli emas: `wrap_id` dagi `/` yoki `?` so'rovni
        // boshqa endpoint'ga (Bearer token bilan) burib yuborardi.
        var c = Case(1);
        var server = new Server("a/b?c", c);

        await Run(server, new ScriptedPrompt(c.GetProperty("passphrase").GetString()!, "y"));

        var fetch = Assert.Single(server.Requests, u => u.AbsolutePath.StartsWith("/api/v1/keys/wraps/"));
        Assert.Equal("/api/v1/keys/wraps/a%2Fb%3Fc", fetch.AbsolutePath);
        Assert.Equal("", fetch.Query);
    }
}
