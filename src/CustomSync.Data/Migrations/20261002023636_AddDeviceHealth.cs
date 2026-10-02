using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_health",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "text", nullable: false),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    rss_bytes = table.Column<long>(type: "bigint", nullable: false),
                    memory_limit_bytes = table.Column<long>(type: "bigint", nullable: true),
                    cache_db_bytes = table.Column<long>(type: "bigint", nullable: false),
                    media_store_bytes = table.Column<long>(type: "bigint", nullable: false),
                    media_store_files = table.Column<long>(type: "bigint", nullable: false),
                    tdlib_files_bytes = table.Column<long>(type: "bigint", nullable: true),
                    tdlib_database_bytes = table.Column<long>(type: "bigint", nullable: true),
                    free_disk_bytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_health", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_health_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "device_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_health");
        }
    }
}
