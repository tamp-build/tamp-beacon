using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon;

/// <summary>
/// EF Core context backed by Postgres. Two table groups:
/// <list type="bullet">
///   <item>Telemetry tables (<see cref="Builds"/>, <see cref="Targets"/>,
///         <see cref="Commands"/>, <see cref="Events"/>) — populated from the
///         OTLP ingest path; mapped here so the schema migrates with the
///         rest of the model, but the receiver wiring lands in Slice 4.</item>
///   <item>Auth tables (<see cref="Users"/>, <see cref="Projects"/>,
///         <see cref="ProjectMembers"/>, <see cref="ProjectTokens"/>,
///         <see cref="IdentityProviders"/>, <see cref="IdentityProviderLinks"/>,
///         <see cref="SetupStateEntries"/>, <see cref="AuthAuditLog"/>) — the
///         TAM-214 model. Slice 1 ships the schema + the setup-state +
///         setup-token bootstrap.</item>
/// </list>
/// Snake-case columns; identifiers stay PascalCase in the model. Column
/// types are tuned for Postgres — <c>bigint</c> ids, <c>text</c> for
/// variable strings, <c>timestamptz</c> for <see cref="System.DateTimeOffset"/>.
/// </summary>
public sealed class BeaconDbContext(DbContextOptions<BeaconDbContext> options) : DbContext(options)
{
    public DbSet<Build> Builds => Set<Build>();
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public DbSet<SetupState> SetupStateEntries => Set<SetupState>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectToken> ProjectTokens => Set<ProjectToken>();
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();
    public DbSet<IdentityProviderLink> IdentityProviderLinks => Set<IdentityProviderLink>();
    public DbSet<AuthAuditLogEntry> AuthAuditLog => Set<AuthAuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureTelemetry(modelBuilder);
        ConfigureAuth(modelBuilder);
    }

    private static void ConfigureTelemetry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Build>(b =>
        {
            b.ToTable("builds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Seq).HasColumnName("seq");
            b.Property(x => x.ProjectId).HasColumnName("project_id");
            b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.ProjectId).HasDatabaseName("ix_builds_project_id");
            b.Property(x => x.Organization).HasColumnName("organization").IsRequired();
            b.Property(x => x.ProjectName).HasColumnName("project_name").IsRequired();
            b.Property(x => x.ProjectArea).HasColumnName("project_area");
            b.Property(x => x.CliVersion).HasColumnName("cli_version");
            b.Property(x => x.StartedUnixNs).HasColumnName("started_unix_ns");
            b.Property(x => x.DurationNs).HasColumnName("duration_ns");
            b.Property(x => x.ExitCode).HasColumnName("exit_code");
            b.Property(x => x.Outcome).HasColumnName("outcome").IsRequired();
            b.Property(x => x.TargetsTotal).HasColumnName("targets_total");
            b.Property(x => x.TargetsFailed).HasColumnName("targets_failed");
            b.Property(x => x.CommandsTotal).HasColumnName("commands_total");
            b.Property(x => x.FailureTarget).HasColumnName("failure_target");
            b.Property(x => x.HostOs).HasColumnName("host_os");
            b.Property(x => x.HostArch).HasColumnName("host_arch");
            b.Property(x => x.CiVendor).HasColumnName("ci_vendor");
            b.Property(x => x.PeakMemoryBytes).HasColumnName("peak_memory_b");
            b.Property(x => x.RawTags).HasColumnName("raw_tags").HasColumnType("jsonb").IsRequired();

            b.HasIndex(x => x.Seq).IsUnique().HasDatabaseName("ix_builds_seq");
            b.HasIndex(x => x.StartedUnixNs).HasDatabaseName("ix_builds_started");
            b.HasIndex(x => new { x.ProjectName, x.ProjectArea }).HasDatabaseName("ix_builds_project");
            b.HasIndex(x => new { x.Outcome, x.StartedUnixNs }).HasDatabaseName("ix_builds_outcome");
        });

        modelBuilder.Entity<Target>(b =>
        {
            b.ToTable("targets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.BuildId).HasColumnName("build_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Phase).HasColumnName("phase");
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.StartedUnixNs).HasColumnName("started_unix_ns");
            b.Property(x => x.DurationNs).HasColumnName("duration_ns");
            b.Property(x => x.CpuTimeMs).HasColumnName("cpu_time_ms");
            b.Property(x => x.GcAllocatedBytes).HasColumnName("gc_allocated_b");
            b.Property(x => x.GcGen0).HasColumnName("gc_gen0");
            b.Property(x => x.GcGen1).HasColumnName("gc_gen1");
            b.Property(x => x.GcGen2).HasColumnName("gc_gen2");
            b.Property(x => x.CommandsCount).HasColumnName("commands_count");
            b.Property(x => x.RawTags).HasColumnName("raw_tags").HasColumnType("jsonb").IsRequired();

            b.HasOne(x => x.Build).WithMany(x => x.Targets).HasForeignKey(x => x.BuildId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.BuildId).HasDatabaseName("ix_targets_build");
            b.HasIndex(x => x.Name).HasDatabaseName("ix_targets_name");
        });

        modelBuilder.Entity<Command>(b =>
        {
            b.ToTable("commands");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.TargetId).HasColumnName("target_id");
            b.Property(x => x.Executable).HasColumnName("executable").IsRequired();
            b.Property(x => x.ArgsCount).HasColumnName("args_count");
            b.Property(x => x.ExitCode).HasColumnName("exit_code");
            b.Property(x => x.DurationNs).HasColumnName("duration_ns");
            b.Property(x => x.CpuTotalMs).HasColumnName("cpu_total_ms");
            b.Property(x => x.PeakMemoryBytes).HasColumnName("peak_memory_b");
            b.Property(x => x.StdoutBytes).HasColumnName("stdout_bytes");
            b.Property(x => x.StderrBytes).HasColumnName("stderr_bytes");
            b.Property(x => x.RawTags).HasColumnName("raw_tags").HasColumnType("jsonb").IsRequired();

            b.HasOne(x => x.Target).WithMany(x => x.Commands).HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.TargetId).HasDatabaseName("ix_commands_target");
            b.HasIndex(x => x.Executable).HasDatabaseName("ix_commands_exe");
        });

        modelBuilder.Entity<Event>(b =>
        {
            b.ToTable("events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.BuildId).HasColumnName("build_id");
            b.Property(x => x.TargetId).HasColumnName("target_id");
            b.Property(x => x.CommandId).HasColumnName("command_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.AtUnixNs).HasColumnName("at_unix_ns");
            b.Property(x => x.RawTags).HasColumnName("raw_tags").HasColumnType("jsonb").IsRequired();

            b.HasOne(x => x.Build).WithMany(x => x.Events).HasForeignKey(x => x.BuildId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Command).WithMany().HasForeignKey(x => x.CommandId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.BuildId).HasDatabaseName("ix_events_build");
        });

        modelBuilder.Entity<PushSubscription>(b =>
        {
            b.ToTable("push_subscriptions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Endpoint).HasColumnName("endpoint").IsRequired();
            b.Property(x => x.P256dh).HasColumnName("p256dh").IsRequired();
            b.Property(x => x.Auth).HasColumnName("auth").IsRequired();
            b.Property(x => x.ProjectFilter).HasColumnName("project_filter");
            b.Property(x => x.AreaFilter).HasColumnName("area_filter");
            b.Property(x => x.CreatedUnixNs).HasColumnName("created_unix_ns");

            b.HasIndex(x => x.Endpoint).IsUnique().HasDatabaseName("ix_push_endpoint");
        });
    }

    private static void ConfigureAuth(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SetupState>(b =>
        {
            b.ToTable("setup_state");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.IsComplete).HasColumnName("is_complete");
            b.Property(x => x.PendingTokenHash).HasColumnName("pending_token_hash");
            b.Property(x => x.PendingTokenIssuedAt).HasColumnName("pending_token_issued_at");
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Username).HasColumnName("username").IsRequired();
            b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
            b.Property(x => x.PasswordHash).HasColumnName("password_hash");
            b.Property(x => x.IsSystemAdmin).HasColumnName("is_system_admin");
            b.Property(x => x.IsDisabled).HasColumnName("is_disabled");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            b.Property(x => x.PendingResetHash).HasColumnName("pending_reset_hash");
            b.Property(x => x.PendingResetIssuedAt).HasColumnName("pending_reset_issued_at");

            b.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ix_users_username");
        });

        modelBuilder.Entity<Project>(b =>
        {
            b.ToTable("projects");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Slug).HasColumnName("slug").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.ArchivedAt).HasColumnName("archived_at");

            b.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ix_projects_slug");
        });

        modelBuilder.Entity<ProjectMember>(b =>
        {
            b.ToTable("project_members");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.ProjectId).HasColumnName("project_id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.Role).HasColumnName("role").HasConversion<int>();
            b.Property(x => x.AddedAt).HasColumnName("added_at");

            b.HasOne(x => x.Project).WithMany(x => x.Members).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.User).WithMany(x => x.ProjectMemberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique().HasDatabaseName("ix_project_members_pair");
        });

        modelBuilder.Entity<ProjectToken>(b =>
        {
            b.ToTable("project_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.ProjectId).HasColumnName("project_id");
            b.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
            b.Property(x => x.Label).HasColumnName("label").IsRequired();
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");

            b.HasOne(x => x.Project).WithMany(x => x.Tokens).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ix_project_tokens_hash");
            b.HasIndex(x => x.ProjectId).HasDatabaseName("ix_project_tokens_project");
        });

        modelBuilder.Entity<IdentityProvider>(b =>
        {
            b.ToTable("identity_providers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
            b.Property(x => x.Issuer).HasColumnName("issuer").IsRequired();
            b.Property(x => x.Audience).HasColumnName("audience").IsRequired();
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(x => x.Kind).IsUnique().HasDatabaseName("ix_identity_providers_kind");
        });

        modelBuilder.Entity<IdentityProviderLink>(b =>
        {
            b.ToTable("identity_provider_links");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.Subject).HasColumnName("subject").IsRequired();
            b.Property(x => x.LinkedAt).HasColumnName("linked_at");

            b.HasOne(x => x.User).WithMany(x => x.IdentityProviderLinks).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Provider).WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.ProviderId, x.Subject }).IsUnique().HasDatabaseName("ix_identity_provider_links_sub");
        });

        modelBuilder.Entity<AuthAuditLogEntry>(b =>
        {
            b.ToTable("auth_audit_log");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Event).HasColumnName("event").IsRequired();
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            b.Property(x => x.DetailJson).HasColumnName("detail_json").HasColumnType("jsonb");
            b.Property(x => x.RemoteIp).HasColumnName("remote_ip");
            b.Property(x => x.AtUtc).HasColumnName("at_utc");

            b.HasOne(x => x.ActorUser).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(x => x.AtUtc).HasDatabaseName("ix_auth_audit_log_at");
            b.HasIndex(x => x.Event).HasDatabaseName("ix_auth_audit_log_event");
        });
    }
}
