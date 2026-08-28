using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTombstoneTargetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_records_tombstone_target",
                table: "records",
                column: "target_record_id",
                filter: "target_record_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_records_tombstone_target",
                table: "records");
        }
    }
}
