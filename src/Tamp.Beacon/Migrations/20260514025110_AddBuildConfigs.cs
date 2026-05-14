using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TAM-215 — Build now routes through a BuildConfig under the project.
            // We're pre-1.0 with no production data we care about; rather than
            // backfilling we wipe the telemetry tables and let adopters re-emit.
            // CASCADE drops the dependent targets / commands / events rows in one
            // sweep. PushSubscriptions are unaffected (they FK to user + project).
            migrationBuilder.Sql("TRUNCATE TABLE builds CASCADE;");

            migrationBuilder.CreateTable(
                name: "build_configs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_build_configs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddColumn<long>(
                name: "build_config_id",
                table: "builds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_builds_build_config_id",
                table: "builds",
                column: "build_config_id");

            migrationBuilder.CreateIndex(
                name: "ix_build_configs_project_slug",
                table: "build_configs",
                columns: new[] { "project_id", "slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_builds_build_configs_build_config_id",
                table: "builds",
                column: "build_config_id",
                principalTable: "build_configs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_builds_build_configs_build_config_id",
                table: "builds");

            migrationBuilder.DropTable(
                name: "build_configs");

            migrationBuilder.DropIndex(
                name: "ix_builds_build_config_id",
                table: "builds");

            migrationBuilder.DropColumn(
                name: "build_config_id",
                table: "builds");
        }
    }
}
