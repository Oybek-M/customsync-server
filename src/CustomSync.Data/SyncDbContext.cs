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
    public DbSet<ReleaseEntity>        Releases        => Set<ReleaseEntity>();
    public DbSet<ReleaseMirrorEntity>  ReleaseMirrors  => Set<ReleaseMirrorEntity>();
    public DbSet<UploadSessionEntity>  UploadSessions  => Set<UploadSessionEntity>();
    public DbSet<RetentionPolicyEntity> RetentionPolicies => Set<RetentionPolicyEntity>();
    public DbSet<ArchiveRunEntity>     ArchiveRuns       => Set<ArchiveRunEntity>();

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
            // Har push tombstone nishonini tekshiradi. Qisman indeks:
            // target_record_id faqat tombstone'larda to'ldiriladi, ya'ni
            // indeks kichik qoladi va hot-path'ga yuk tushirmaydi.
            e.HasIndex(x => x.TargetRecordId)
             .HasDatabaseName("idx_records_tombstone_target")
             .HasFilter("target_record_id IS NOT NULL");
        });

        b.Entity<MediaBlobEntity>(e =>
        {
            e.ToTable("media_blobs");
            e.HasKey(x => x.Hash);
            e.HasIndex(x => x.OrphanedAt).HasFilter("orphaned_at IS NOT NULL");
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

        b.Entity<ReleaseEntity>(e =>
        {
            e.ToTable("releases");
            e.HasKey(x => x.ReleaseId);
            e.HasIndex(x => new { x.Platform, x.Version, x.Channel }).IsUnique();
        });

        b.Entity<ReleaseMirrorEntity>(e =>
        {
            e.ToTable("release_mirrors");
            e.HasKey(x => new { x.ReleaseId, x.Mirror });
            e.HasOne(x => x.Release)
             .WithMany(r => r.Mirrors)
             .HasForeignKey(x => x.ReleaseId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UploadSessionEntity>(e =>
        {
            e.ToTable("upload_sessions");
            e.HasKey(x => x.SessionId);
            e.HasIndex(x => x.ReleaseId);
        });

        b.Entity<RetentionPolicyEntity>(e =>
        {
            e.ToTable("retention_policies");
            e.HasKey(x => x.PolicyId);
            e.HasIndex(x => x.Priority);
        });

        b.Entity<ArchiveRunEntity>(e =>
        {
            e.ToTable("archive_runs");
            e.HasKey(x => x.RunId);
            e.Property(x => x.RunId).ValueGeneratedOnAdd();
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.PolicyId);
        });
    }
}
