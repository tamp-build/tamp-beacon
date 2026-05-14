using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tamp.Beacon.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "builds",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    project_name = table.Column<string>(type: "text", nullable: false),
                    project_area = table.Column<string>(type: "text", nullable: true),
                    organization = table.Column<string>(type: "text", nullable: false),
                    cli_version = table.Column<string>(type: "text", nullable: true),
                    started_unix_ns = table.Column<long>(type: "bigint", nullable: false),
                    duration_ns = table.Column<long>(type: "bigint", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    targets_total = table.Column<int>(type: "integer", nullable: false),
                    targets_failed = table.Column<int>(type: "integer", nullable: false),
                    commands_total = table.Column<int>(type: "integer", nullable: false),
                    failure_target = table.Column<string>(type: "text", nullable: true),
                    host_os = table.Column<string>(type: "text", nullable: true),
                    host_arch = table.Column<string>(type: "text", nullable: true),
                    ci_vendor = table.Column<string>(type: "text", nullable: true),
                    peak_memory_b = table.Column<long>(type: "bigint", nullable: false),
                    raw_tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_builds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_providers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    audience = table.Column<string>(type: "text", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    p256dh = table.Column<string>(type: "text", nullable: false),
                    auth = table.Column<string>(type: "text", nullable: false),
                    project_filter = table.Column<string>(type: "text", nullable: true),
                    area_filter = table.Column<string>(type: "text", nullable: true),
                    created_unix_ns = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setup_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    pending_token_hash = table.Column<string>(type: "text", nullable: true),
                    pending_token_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    is_system_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_disabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "targets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    build_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_unix_ns = table.Column<long>(type: "bigint", nullable: false),
                    duration_ns = table.Column<long>(type: "bigint", nullable: false),
                    cpu_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    gc_allocated_b = table.Column<long>(type: "bigint", nullable: false),
                    gc_gen0 = table.Column<int>(type: "integer", nullable: false),
                    gc_gen1 = table.Column<int>(type: "integer", nullable: false),
                    gc_gen2 = table.Column<int>(type: "integer", nullable: false),
                    commands_count = table.Column<int>(type: "integer", nullable: false),
                    raw_tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_targets", x => x.id);
                    table.ForeignKey(
                        name: "FK_targets_builds_build_id",
                        column: x => x.build_id,
                        principalTable: "builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "auth_audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true),
                    remote_ip = table.Column<string>(type: "text", nullable: true),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_auth_audit_log_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "identity_provider_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<long>(type: "bigint", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_provider_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_identity_provider_links_identity_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "identity_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_identity_provider_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.ForeignKey(
                        name: "FK_projects_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commands",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    executable = table.Column<string>(type: "text", nullable: false),
                    args_count = table.Column<int>(type: "integer", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: false),
                    duration_ns = table.Column<long>(type: "bigint", nullable: false),
                    cpu_total_ms = table.Column<double>(type: "double precision", nullable: false),
                    peak_memory_b = table.Column<long>(type: "bigint", nullable: false),
                    stdout_bytes = table.Column<long>(type: "bigint", nullable: false),
                    stderr_bytes = table.Column<long>(type: "bigint", nullable: false),
                    raw_tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commands", x => x.id);
                    table.ForeignKey(
                        name: "FK_commands_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_tokens_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_tokens_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    build_id = table.Column<long>(type: "bigint", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: true),
                    command_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    at_unix_ns = table.Column<long>(type: "bigint", nullable: false),
                    raw_tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_builds_build_id",
                        column: x => x.build_id,
                        principalTable: "builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_events_commands_command_id",
                        column: x => x.command_id,
                        principalTable: "commands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_events_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auth_audit_log_actor_user_id",
                table: "auth_audit_log",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_at",
                table: "auth_audit_log",
                column: "at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_event",
                table: "auth_audit_log",
                column: "event");

            migrationBuilder.CreateIndex(
                name: "ix_builds_outcome",
                table: "builds",
                columns: new[] { "outcome", "started_unix_ns" });

            migrationBuilder.CreateIndex(
                name: "ix_builds_project",
                table: "builds",
                columns: new[] { "project_name", "project_area" });

            migrationBuilder.CreateIndex(
                name: "ix_builds_seq",
                table: "builds",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_builds_started",
                table: "builds",
                column: "started_unix_ns");

            migrationBuilder.CreateIndex(
                name: "ix_commands_exe",
                table: "commands",
                column: "executable");

            migrationBuilder.CreateIndex(
                name: "ix_commands_target",
                table: "commands",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_command_id",
                table: "events",
                column: "command_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_target_id",
                table: "events",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_build",
                table: "events",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_provider_links_user_id",
                table: "identity_provider_links",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_provider_links_sub",
                table: "identity_provider_links",
                columns: new[] { "provider_id", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_providers_kind",
                table: "identity_providers",
                column: "kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_members_user_id",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_pair",
                table: "project_members",
                columns: new[] { "project_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_tokens_created_by_user_id",
                table: "project_tokens",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_tokens_hash",
                table: "project_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_tokens_project",
                table: "project_tokens",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_created_by_user_id",
                table: "projects",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_slug",
                table: "projects",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_endpoint",
                table: "push_subscriptions",
                column: "endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_targets_build",
                table: "targets",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "ix_targets_name",
                table: "targets",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_audit_log");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "identity_provider_links");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "project_tokens");

            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropTable(
                name: "setup_state");

            migrationBuilder.DropTable(
                name: "commands");

            migrationBuilder.DropTable(
                name: "identity_providers");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "targets");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "builds");
        }
    }
}
