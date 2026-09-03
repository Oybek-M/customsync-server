using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    release_id = table.Column<string>(type: "text", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    uploaded_by = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_releases", x => x.release_id);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "text", nullable: false),
                    release_id = table.Column<string>(type: "text", nullable: false),
                    total_size = table.Column<long>(type: "bigint", nullable: false),
                    received = table.Column<long>(type: "bigint", nullable: false),
                    temp_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "release_mirrors",
                columns: table => new
                {
                    release_id = table.Column<string>(type: "text", nullable: false),
                    mirror = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_mirrors", x => new { x.release_id, x.mirror });
                    table.ForeignKey(
                        name: "fk_release_mirrors_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "release_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_releases_platform_version_channel",
                table: "releases",
                columns: new[] { "platform", "version", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_release_id",
                table: "upload_sessions",
                column: "release_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_mirrors");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "releases");
        }
    }
}
