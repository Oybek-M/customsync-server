using System.Text;
using System.Text.Json.Nodes;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Tekshiruv bosqichida qo'shilgan testlar (2026-09-17). Har biri delegate
/// ishida qamrab olinmagan talabni yopadi.
/// </summary>
public class CaptureVerificationTests
{
    // Har bir chaqiruvda istisno tashlaydigan transport: qabul siklining
    // xatoga qanday munosabatda bo'lishini o'lchaydi.
    private sealed class ThrowingTransport : ITdTransport
    {
        private int _receiveCalls;
        public int ReceiveCalls => Volatile.Read(ref _receiveCalls);
        public int CreateClientId() => 1;
        public void Send(int clientId, string requestJson) { }
        public string? Receive(double timeoutSeconds)
        {
            Interlocked.Increment(ref _receiveCalls);
            throw new InvalidOperationException("transport uzildi");
        }
        public string? Execute(string requestJson) => null;
        public void Dispose() { }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
        public string AllText { get { lock (Lines) return string.Join("\n", Lines); } }
    }

    // 16. UTF-8 marshalling. Butun interop qatlami sinovsiz qolgan edi:
    // ANSI marshalling ishlatilsa lotin bo'lmagan matn buziladi — ya'ni
    // aynan shu xizmat ushlab qolish uchun yaratilgan xabarlar.
    [Fact]
    public void Test16_Utf8_marshalling_round_trips_non_ascii_text()
    {
        const string text = "Salom, дунё! Привет 🌍 — o'chirilgan xabar";

        var ptr = TdMarshal.StringToUtf8Ptr(text);
        try
        {
            Assert.Equal(text, TdMarshal.PtrToUtf8String(ptr));

            // Bayt uzunligi UTF-8 ga teng bo'lishi shart. ANSI marshalling
            // bu tekshiruvdan o'tmaydi (har belgi bitta baytga siqiladi).
            var length = 0;
            while (System.Runtime.InteropServices.Marshal.ReadByte(ptr, length) != 0) length++;
            Assert.Equal(Encoding.UTF8.GetByteCount(text), length);
        }
        finally
        {
            TdMarshal.FreeUtf8Ptr(ptr);
        }

        Assert.Null(TdMarshal.PtrToUtf8String(IntPtr.Zero));
    }

    // 17. `TdRedactor` ishlab chiqarish kodida chaqirilmasa, u shunchaki
    // o'lik kod: keyingi task'da qo'yilgan birinchi `LogDebug(raw)` login
    // kodini va api_hash'ni journal'ga yozadi.
    [Fact]
    public async Task Test17_Client_logs_payloads_only_through_the_redactor()
    {
        var transport = new FakeTdTransport();
        transport.OnSend = (_, reqJson) =>
        {
            var extra = JsonNode.Parse(reqJson)!["@extra"]!.ToString();
            transport.EnqueueIncoming(new JsonObject
            {
                ["@type"] = "ok",
                ["@extra"] = extra,
                ["password"] = "javob_ichidagi_parol"
            }.ToJsonString());
        };

        var logger = new CapturingLogger<TdClient>();
        using var client = new TdClient(transport, logger);

        var request = new JsonObject
        {
            ["@type"] = "setTdlibParameters",
            ["api_hash"] = "maxfiy_api_hash",
            ["phone_number"] = "+998901234567",
            ["code"] = "54321"
        }.ToJsonString();

        await client.SendAsync(request, TimeSpan.FromSeconds(5));

        var logged = logger.AllText;
        Assert.DoesNotContain("maxfiy_api_hash", logged);
        Assert.DoesNotContain("+998901234567", logged);
        Assert.DoesNotContain("54321", logged);
        Assert.DoesNotContain("javob_ichidagi_parol", logged);

        // Log foydali bo'lib qolishi kerak: kalitlar va @type joyida.
        Assert.Contains("api_hash", logged);
        Assert.Contains("setTdlibParameters", logged);
    }

    // 18. Xizmatning o'zi: sessiya avtorizatsiya qilinmagan bo'lsa
    // fon rejimida so'ramaydi, `--login` ni aytadi va NOL BO'LMAGAN kod
    // bilan chiqadi. Nol kod bilan chiqish systemd uchun "muvaffaqiyat" —
    // buzilgan xizmat sog'lom ko'rinadi va qayta ishga tushirilmaydi.
    [Fact]
    public async Task Test18_Unauthorized_headless_start_stops_with_nonzero_exit_and_names_login()
    {
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        var prompt = new FakeConsolePrompt();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "fake_hash"
        }).Build();

        var auth = new TdAuthenticator(client, config, prompt, isInteractive: false);
        var gate = new AuthorizationGate(client, auth);

        var run = gate.RunAsync(TimeSpan.FromSeconds(5));
        transport.EnqueueIncoming(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitPhoneNumber" }
        }.ToJsonString());

        var outcome = await run;

        Assert.False(outcome.Ready);
        Assert.Contains("--login", outcome.Message);
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.Equal(0, prompt.PromptCallCount);
    }

    // 18b. Avtorizatsiya tayyor bo'lsa xizmat ishlashda davom etadi.
    [Fact]
    public async Task Test18b_Ready_state_lets_the_service_continue()
    {
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Telegram:ApiId"] = "1", ["Telegram:ApiHash"] = "h" }).Build();
        var auth = new TdAuthenticator(client, config, new FakeConsolePrompt(), isInteractive: false);
        var gate = new AuthorizationGate(client, auth);

        var run = gate.RunAsync(TimeSpan.FromSeconds(5));
        transport.EnqueueIncoming(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
        }.ToJsonString());

        var outcome = await run;

        Assert.True(outcome.Ready);
        Assert.Equal(0, outcome.ExitCode);
    }

    // 19. Qabul sikli xatoda darhol qayta urinsa, kutubxona uzilganda
    // 24/7 jarayon protsessorni 100% band qiladi.
    [Fact]
    public void Test19_Receive_loop_backs_off_instead_of_spinning_on_errors()
    {
        var transport = new ThrowingTransport();
        using (var client = new TdClient(transport, errorBackoff: TimeSpan.FromMilliseconds(50)))
        {
            Thread.Sleep(300);
        }

        // 300 ms / 50 ms ≈ 6 urinish. Kechikishsiz bu minglab bo'lardi.
        Assert.InRange(transport.ReceiveCalls, 1, 25);
    }

    // 20. Preflight: papka yaratib bo'lmasa aniq xabar beradi va
    // ishlov berilmagan istisno tashlamaydi.
    [Fact]
    public void Test20_Preflight_reports_an_unusable_directory()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"cs-capture-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "men papka emasman");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "fake_hash",
                // Fayl papka sifatida berilgan -> yaratib bo'lmaydi.
                ["Telegram:DatabaseDirectory"] = blocker,
                ["Telegram:FilesDirectory"] = Path.GetTempPath()
            }).Build();

            var report = CapturePreflight.Check(config, nativeLibChecker: _ => true);

            Assert.False(report.Success);
            Assert.Contains(report.Errors, e => e.Contains("DatabaseDirectory"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
