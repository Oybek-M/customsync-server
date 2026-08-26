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
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    enrolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_cursor = table.Column<long>(type: "bigint", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    refresh_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.device_id);
                });

            migrationBuilder.CreateTable(
                name: "enrollment_codes",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_codes", x => x.code_hash);
                });

            migrationBuilder.CreateTable(
                name: "key_wraps",
                columns: table => new
                {
                    wrap_id = table.Column<string>(type: "text", nullable: false),
                    wrap_type = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    iterations = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_wraps", x => x.wrap_id);
                });

            migrationBuilder.CreateTable(
                name: "media_blobs",
                columns: table => new
                {
                    hash = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_blobs", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "record_media",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_media", x => new { x.record_id, x.hash });
                });

            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    account_hash = table.Column<string>(type: "text", nullable: false),
                    peer_hash = table.Column<string>(type: "text", nullable: false),
                    msg_id = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<long>(type: "bigint", nullable: false),
                    observed_at = table.Column<long>(type: "bigint", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_size = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_records", x => x.record_id);
                });

            migrationBuilder.CreateTable(
                name: "server_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    value_type = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_settings", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "idx_records_account",
                table: "records",
                columns: new[] { "account_hash", "occurred_at", "seq" });

            migrationBuilder.CreateIndex(
                name: "idx_records_kind",
                table: "records",
                columns: new[] { "kind", "occurred_at", "seq" });

            migrationBuilder.CreateIndex(
                name: "idx_records_occur",
                table: "records",
                columns: new[] { "occurred_at", "seq" });

            migrationBuilder.CreateIndex(
                name: "idx_records_peer",
                table: "records",
                columns: new[] { "peer_hash", "occurred_at", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_records_seq",
                table: "records",
                column: "seq",
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
