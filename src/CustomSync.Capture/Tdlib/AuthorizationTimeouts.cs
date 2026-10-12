namespace CustomSync.Capture.Tdlib;

/// <summary>
/// `AuthorizationGate` qancha kutadi. Fon rejimida avtorizatsiya holati
/// darhol keladi — uzoq kutish faqat ishlamayotgan xizmatni yashiradi.
/// `--login` da esa egasi raqam, kod va 2FA parolni qo'lda kiritadi:
/// 2 daqiqa yetmasligi mumkin, chala qolgan urinish esa Telegram'dan
/// yangi kod so'rashga majbur qiladi.
/// </summary>
public static class AuthorizationTimeouts
{
    public static readonly TimeSpan Headless = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Interactive = TimeSpan.FromMinutes(15);

    public static TimeSpan For(bool isInteractive) => isInteractive ? Interactive : Headless;
}
