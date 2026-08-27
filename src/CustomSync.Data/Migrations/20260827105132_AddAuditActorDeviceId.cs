using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditActorDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_device_id",
                table: "audit_log",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_audit_actor",
                table: "audit_log",
                column: "actor_device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_audit_actor",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "actor_device_id",
                table: "audit_log");
        }
    }
}
