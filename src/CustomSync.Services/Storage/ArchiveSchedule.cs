using System.Globalization;

namespace CustomSync.Services.Storage;

public static class ArchiveSchedule
{
    public const int DefaultHour = 3;
    public const int DefaultMinute = 30;

    /// <summary>
    /// Jadval sozlamalarini xom matndan o'qiydi va tekshiradi.
    /// Sozlama qiymatlari admin endpoint'i orqali HAR QANDAY matn bo'lishi
    /// mumkin (tip tekshiruvi yo'q), shuning uchun bu yerda `TryParse` ishlatiladi:
    /// buzuq qiymat fon xizmatini yiqitmasligi, lekin SABABI aytilib
    /// audit'ga tushishi kerak. Aks holda `jobs_hour = 25` rejalashtirilgan
    /// tozalashni hech qanday signalsiz butunlay o'chirib qo'yadi.
    /// </summary>
    public static bool TryResolve(
        string? hourRaw,
        string? minuteRaw,
        out int hour,
        out int minute,
        out string? error)
    {
        hour = DefaultHour;
        minute = DefaultMinute;
        error = null;

        if (!TryParseUnit(hourRaw, "storage.jobs_hour", 0, 23, out hour, out error))
        {
            hour = DefaultHour;
            minute = DefaultMinute;
            return false;
        }

        if (!TryParseUnit(minuteRaw, "storage.jobs_minute", 0, 59, out minute, out error))
        {
            hour = DefaultHour;
            minute = DefaultMinute;
            return false;
        }

        return true;
    }

    private static bool TryParseUnit(
        string? raw, string key, int min, int max, out int value, out string? error)
    {
        value = 0;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{key}: '{raw}' butun son emas";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{key}: {value} oralig'i {min}..{max} dan tashqarida";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Jadval bo'yicha ish ishga tushishi kerakligini aniqlaydi.
    /// Bugungi hour:minute UTC o'tgan bo'lsa VA bugun hali ishga tushmagan
    /// bo'lsa (lastRunDate != today) -> true.
    /// Noto'g'ri hour yoki minute (0..23 va 0..59 dan tashqarida) -> false.
    /// </summary>
    public static bool IsDue(DateTime nowUtc, int hour, int minute, DateOnly? lastRunDate)
    {
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(nowUtc);
        if (lastRunDate.HasValue && lastRunDate.Value == today)
        {
            return false;
        }

        var scheduleTime = new TimeSpan(hour, minute, 0);
        return nowUtc.TimeOfDay >= scheduleTime;
    }
}
