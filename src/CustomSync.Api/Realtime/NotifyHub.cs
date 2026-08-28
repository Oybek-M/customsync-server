namespace CustomSync.Api.Realtime;

/// <summary>
/// Task 6 da WebSocket orqali haqiqiy implementatsiya qilinadi.
/// Hozircha vaqtinchalik stub — push muvaffaqiyatli bo'lganda
/// boshqa qurilmalarni yangilanish haqida xabardor qilish uchun.
/// </summary>
public class NotifyHub
{
    public Task NotifyOthersAsync(string originDeviceId, long seq)
        => Task.CompletedTask;
}
