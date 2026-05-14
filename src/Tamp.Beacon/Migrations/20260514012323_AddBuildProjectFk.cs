using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildProjectFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "project_id",
                table: "builds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_builds_project_id",
                table: "builds",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_builds_projects_project_id",
                table: "builds",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_builds_projects_project_id",
                table: "builds");

            migrationBuilder.DropIndex(
                name: "ix_builds_project_id",
                table: "builds");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "builds");
        }
    }
}
