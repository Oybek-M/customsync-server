using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Data;

public class SyncDbContext(DbContextOptions<SyncDbContext> options)
    : DbContext(options)
{
    public DbSet<DeviceEntity>         Devices         => Set<DeviceEntity>();
    public DbSet<RecordEntity>         Records         => Set<RecordEntity>();
    public DbSet<MediaBlobEntity>      MediaBlobs      => Set<MediaBlobEntity>();
    public DbSet<RecordMediaEntity>    RecordMedia     => Set<RecordMediaEntity>();
    public DbSet<KeyWrapEntity>        KeyWraps        => Set<KeyWrapEntity>();
    public DbSet<ServerSettingEntity>  ServerSettings  => Set<ServerSettingEntity>();
    public DbSet<EnrollmentCodeEntity> EnrollmentCodes => Set<EnrollmentCodeEntity>();
    public DbSet<AuditLogEntity>       AuditLogs       => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeviceEntity>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
        });

        b.Entity<RecordEntity>(e =>
        {
            e.ToTable("records");
            e.HasKey(x => x.RecordId);
            e.HasIndex(x => x.Seq).IsUnique();
            e.HasIndex(x => new { x.PeerHash, x.OccurredAt, x.Seq })
             .HasDatabaseName("idx_records_peer");
            e.HasIndex(x => new { x.AccountHash, x.OccurredAt, x.Seq })
             .HasDatabaseName("idx_records_account");
            e.HasIndex(x => new { x.Kind, x.OccurredAt, x.Seq })
             .HasDatabaseName("idx_records_kind");
            e.HasIndex(x => new { x.OccurredAt, x.Seq })
             .HasDatabaseName("idx_records_occur");
        });

        b.Entity<MediaBlobEntity>(e =>
        {
            e.ToTable("media_blobs");
            e.HasKey(x => x.Hash);
        });

        b.Entity<RecordMediaEntity>(e =>
        {
            e.ToTable("record_media");
            e.HasKey(x => new { x.RecordId, x.Hash });
        });

        b.Entity<KeyWrapEntity>(e =>
        {
            e.ToTable("key_wraps");
            e.HasKey(x => x.WrapId);
        });

        b.Entity<ServerSettingEntity>(e =>
        {
            e.ToTable("server_settings");
            e.HasKey(x => x.Key);
        });

        b.Entity<EnrollmentCodeEntity>(e =>
        {
            e.ToTable("enrollment_codes");
            e.HasKey(x => x.CodeHash);
        });

        b.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.ActorDeviceId).HasDatabaseName("idx_audit_actor");
        });
    }
}
