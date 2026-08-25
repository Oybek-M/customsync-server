namespace CustomSync.Data.Entities;

/// <summary>
/// Runtime konfiguratsiya (qoida K1). Sozlanadigan har qanday qiymat
/// shu yerda yashaydi — kodda yoki appsettings.json da emas.
/// </summary>
public class ServerSettingEntity
{
    public string   Key         { get; set; } = null!;
    public string   Value       { get; set; } = null!;
    /// <summary>"int" | "bool" | "string" | "duration" — UI validatsiyasi uchun.</summary>
    public string   ValueType   { get; set; } = null!;
    public string   Category    { get; set; } = null!;
    public string   Description { get; set; } = null!;
    public DateTime UpdatedAt   { get; set; }
}
