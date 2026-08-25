using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    EnrolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCursor = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "enrollment_codes",
                columns: table => new
                {
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrollment_codes", x => x.CodeHash);
                });

            migrationBuilder.CreateTable(
                name: "key_wraps",
                columns: table => new
                {
                    WrapId = table.Column<string>(type: "text", nullable: false),
                    WrapType = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_key_wraps", x => x.WrapId);
                });

            migrationBuilder.CreateTable(
                name: "media_blobs",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    StoragePath = table.Column<string>(type: "text", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_blobs", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "record_media",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_media", x => new { x.RecordId, x.Hash });
                });

            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "text", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    PeerHash = table.Column<string>(type: "text", nullable: false),
                    MsgId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    PayloadSize = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_records", x => x.RecordId);
                });

            migrationBuilder.CreateTable(
                name: "server_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    ValueType = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_At",
                table: "audit_log",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "idx_records_kind",
                table: "records",
                columns: new[] { "Kind", "OccurredAt", "Seq" });

            migrationBuilder.CreateIndex(
                name: "idx_records_occur",
                table: "records",
                columns: new[] { "OccurredAt", "Seq" });

            migrationBuilder.CreateIndex(
                name: "idx_records_peer",
                table: "records",
                columns: new[] { "PeerHash", "OccurredAt", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_records_Seq",
                table: "records",
                column: "Seq",
                unique: true);

            // sync_counter EF entity sifatida modellashtirilmaydi -- u faqat
            // raw SQL orqali, `UPDATE ... RETURNING value` bilan ishlatiladi
            // (plan 1b). EF orqali o'qish/yozish poyga holatini yashirar edi.
            migrationBuilder.Sql("""
                CREATE TABLE sync_counter (
                  id    INT PRIMARY KEY CHECK (id = 1),
                  value BIGINT NOT NULL DEFAULT 0
                );
                INSERT INTO sync_counter (id, value) VALUES (1, 0);
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS sync_counter;");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "enrollment_codes");

            migrationBuilder.DropTable(
                name: "key_wraps");

            migrationBuilder.DropTable(
                name: "media_blobs");

            migrationBuilder.DropTable(
                name: "record_media");

            migrationBuilder.DropTable(
                name: "records");

            migrationBuilder.DropTable(
                name: "server_settings");
        }
    }
}
