using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSubscriptionFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "area_filter",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "project_filter",
                table: "push_subscriptions");

            migrationBuilder.AddColumn<long>(
                name: "project_id",
                table: "push_subscriptions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "user_id",
                table: "push_subscriptions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_project_id",
                table: "push_subscriptions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_user_project",
                table: "push_subscriptions",
                columns: new[] { "user_id", "project_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_push_subscriptions_projects_project_id",
                table: "push_subscriptions",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_push_subscriptions_users_user_id",
                table: "push_subscriptions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_push_subscriptions_projects_project_id",
                table: "push_subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_push_subscriptions_users_user_id",
                table: "push_subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_push_subscriptions_project_id",
                table: "push_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_push_user_project",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "push_subscriptions");

            migrationBuilder.AddColumn<string>(
                name: "area_filter",
                table: "push_subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_filter",
                table: "push_subscriptions",
                type: "text",
                nullable: true);
        }
    }
}
