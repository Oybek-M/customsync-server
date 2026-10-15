using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomSync.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshGraceWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "previous_refresh_hash",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "refresh_rotated_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "rotation_replay",
                table: "devices",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "previous_refresh_hash",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "refresh_rotated_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "rotation_replay",
                table: "devices");
        }
    }
}
