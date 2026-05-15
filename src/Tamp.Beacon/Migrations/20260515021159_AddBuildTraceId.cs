using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildTraceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TAM-218 — the orphan-Target race scattered each long CI build
            // across 4-5 rows. The existing telemetry rows on a live deploy
            // are inconsistent in that shape; we drop them so the
            // dashboard reads cleanly post-fix. CASCADE on builds drops
            // targets / commands / events.
            migrationBuilder.Sql("TRUNCATE TABLE builds CASCADE;");

            migrationBuilder.AddColumn<string>(
                name: "trace_id",
                table: "builds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_builds_config_trace",
                table: "builds",
                columns: new[] { "build_config_id", "trace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_builds_config_trace",
                table: "builds");

            migrationBuilder.DropColumn(
                name: "trace_id",
                table: "builds");
        }
    }
}
