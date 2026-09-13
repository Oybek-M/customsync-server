using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveRunsAndOrphanedMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "orphaned_at",
                table: "media_blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "archive_runs",
                columns: table => new
                {
                    run_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    policy_id = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    matched_count = table.Column<int>(type: "integer", nullable: false),
                    deleted_count = table.Column<int>(type: "integer", nullable: false),
                    freed_bytes = table.Column<long>(type: "bigint", nullable: false),
                    missing_media = table.Column<int>(type: "integer", nullable: false),
                    archive_location = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_runs", x => x.run_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_blobs_orphaned_at",
                table: "media_blobs",
                column: "orphaned_at",
                filter: "orphaned_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_archive_runs_policy_id",
                table: "archive_runs",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_runs_started_at",
                table: "archive_runs",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_runs");

            migrationBuilder.DropIndex(
                name: "ix_media_blobs_orphaned_at",
                table: "media_blobs");

            migrationBuilder.DropColumn(
                name: "orphaned_at",
                table: "media_blobs");
        }
    }
}
