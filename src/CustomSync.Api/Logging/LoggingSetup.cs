using Serilog;
using Serilog.Events;

namespace CustomSync.Api.Logging;

/// <summary>
/// Log darajalari bitta joyda: Program.cs ham, testlar ham shu funksiyani
/// ishlatadi, shuning uchun test haqiqiy sozlamani tekshiradi, nusxasini emas.
/// </summary>
public static class LoggingSetup
{
    public static LoggerConfiguration ApplyLevels(this LoggerConfiguration config) => config
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
        // EF har SQL buyrug'ini Information'da yozadi; sync yoqilgach bu har
        // pull siklida jurnalni to'ldirardi. Ogohlantirish va xatolari qoladi.
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning);
}
