using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_reset_hash",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_reset_issued_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_reset_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "pending_reset_issued_at",
                table: "users");
        }
    }
}
