namespace CustomSync.Services.Storage;

public static class ArchiveSchedule
{
    /// <summary>
    /// Jadval bo\'yicha ish ishga tushishi kerakligini aniqlaydi.
    /// Bugungi hour:minute UTC o\'tgan bo\'lsa VA bugun hali ishga tushmagan bo\'lsa (lastRunDate != today) -> true.
    /// Noto\'g\'ri hour yoki minute (0..23 va 0..59 oralig\'idan tashqarida) -> false.
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
