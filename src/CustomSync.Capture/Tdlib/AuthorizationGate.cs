using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

public sealed record AuthorizationOutcome(bool Ready, int ExitCode, string? Message);

/// <summary>
/// `updateAuthorizationState` oqimini `TdAuthenticator` ga ulaydi va xizmat
/// ishlashda davom etishi mumkinmi degan savolga javob beradi.
///
/// Nega alohida sinf: `TdAuthenticator` bitta holatni qayta ishlaydi, lekin
/// xizmatning O'ZI avtorizatsiyani hech qachon tekshirmasa, u avtorizatsiya
/// qilinmagan holatda ham "muvaffaqiyatli ishga tushdi" deb bo'sh aylanadi.
/// Chiqish kodi ham muhim: systemd uchun nol kod "hammasi joyida" degani —
/// buzilgan xizmat sog'lom ko'rinadi va qayta ishga tushirilmaydi.
/// </summary>
public class AuthorizationGate(
    ITdClient client,
    TdAuthenticator authenticator,
    ILogger<AuthorizationGate>? logger = null)
{
    public async Task<AuthorizationOutcome> RunAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Qancha kutilishini kutadigan joyning o'zi aytadi: `--login` da egasi
        // muddatni ko'radi, testlar esa aynan shu qiymatni tekshiradi.
        logger?.LogInformation("Waiting up to {Seconds} seconds for Telegram authorization.", (int)timeout.TotalSeconds);

        var completion = new TaskCompletionSource<AuthorizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async void OnUpdate(string raw)
        {
            try
            {
                if (!IsAuthorizationStateUpdate(raw)) return;

                await authenticator.ProcessAuthorizationStateAsync(raw, ct);

                if (authenticator.IsReady)
                {
                    completion.TrySetResult(new AuthorizationOutcome(true, 0, "ready"));
                }
                else if (authenticator.IsClosed)
                {
                    completion.TrySetResult(new AuthorizationOutcome(
                        false, 78, "TDLib authorization closed."));
                }
            }
            catch (Exception ex)
            {
                // Rad etilgan holat (masalan avtorizatsiya kerak, lekin bu
                // fon rejimi) — xizmat to'xtaydi, xabar sababini aytadi (exit code 78).
                completion.TrySetResult(new AuthorizationOutcome(false, 78, ex.Message));
            }
        }

        client.UpdateReceived += OnUpdate;

        // td_create_client_id bilan yaratilgan TDLib nusxasi birinchi so'rovni
        // olmaguncha hech qanday update yubormaydi (td_json_client.h). Hech narsa
        // yuborilmasa avtorizatsiya holati hech qachon kelmaydi va gate muddat
        // tugaguncha bo'sh kutadi. Obuna yuqorida ulangan, shuning uchun birinchi
        // update yo'qolmaydi.
        _ = WakeAsync(ct);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using (timeoutCts.Token.Register(() => completion.TrySetResult(new AuthorizationOutcome(
                false, 1, $"TDLib did not report an authorization state within {timeout.TotalSeconds:0} seconds."))))
            {
                var outcome = await completion.Task;
                if (!outcome.Ready)
                {
                    logger?.LogError("Authorization not completed: {Message}", outcome.Message);
                }
                return outcome;
            }
        }
        finally
        {
            client.UpdateReceived -= OnUpdate;
        }
    }

    // Javob kerak emas: setTdlibParameters dan oldingi so'rovga TDLib xato bilan
    // javob berishi mumkin, bu esa avtorizatsiyaning muvaffaqiyatsizligi emas.
    private async Task WakeAsync(CancellationToken ct)
    {
        try
        {
            await client.SendAsync("{\"@type\":\"getOption\",\"name\":\"online\"}", ct: ct);
        }
        catch (Exception ex)
        {
            logger?.LogDebug("TDLib wake-up request ended with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private static bool IsAuthorizationStateUpdate(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("@type", out var type)
                && type.GetString() == "updateAuthorizationState";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
