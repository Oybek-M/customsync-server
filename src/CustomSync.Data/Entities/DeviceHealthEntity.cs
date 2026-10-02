namespace CustomSync.Data.Entities;

public class DeviceHealthEntity
{
    public string DeviceId { get; set; } = null!;
    public DeviceEntity? Device { get; set; }
    public DateTime ReportedAt { get; set; }
    public long RssBytes { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public long CacheDbBytes { get; set; }
    public long MediaStoreBytes { get; set; }
    public long MediaStoreFiles { get; set; }
    public long? TdlibFilesBytes { get; set; }
    public long? TdlibDatabaseBytes { get; set; }
    public long? FreeDiskBytes { get; set; }
}
