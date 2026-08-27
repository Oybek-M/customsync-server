namespace CustomSync.Data.Entities;

public class AuditLogEntity
{
    public long     Id       { get; set; }
    public DateTime At       { get; set; }
    /// <summary>Amal QAYSI qurilma ustida bajarilgani.</summary>
    public string?  DeviceId { get; set; }

    /// <summary>
    /// Amalni KIM bajargani. `DeviceId` dan farq qiladi: admin boshqa
    /// qurilmani bekor qilganda ikkalasi turlicha bo'ladi. Alohida
    /// ustun, JSON ichida emas -- web app (plan 03) actor bo'yicha
    /// indeks bilan qidiradi. Enroll'da null: qurilmaning hali
    /// tokeni yo'q.
    /// </summary>
    public string?  ActorDeviceId { get; set; }
    public string   Action   { get; set; } = null!;
    public string?  Detail   { get; set; }
}
