using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTombstoneTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "target_record_id",
                table: "records",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "target_record_id",
                table: "records");
        }
    }
}
