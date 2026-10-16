using CustomSync.Data;
using CustomSync.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CustomSync.Tests;

/// <summary>
/// SettingsService.SetAsync buzuq qiymatni rad etadi. Pastki qatlamlarning
/// fallback'ini sinash uchun (qiymat bazada qo'lda buzilgan holat) qiymat
/// validatsiyani chetlab to'g'ridan-to'g'ri yoziladi.
/// </summary>
internal static class CorruptSetting
{
    public static async Task WriteAsync(IServiceProvider services, string key, string value)
    {
        var db = services.GetRequiredService<SyncDbContext>();
        var changed = await db.ServerSettings.Where(s => s.Key == key)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Value, value));
        if (changed != 1) throw new InvalidOperationException($"setting not found: {key}");
        await services.GetRequiredService<SettingsService>().ReloadCacheAsync();
    }
}
