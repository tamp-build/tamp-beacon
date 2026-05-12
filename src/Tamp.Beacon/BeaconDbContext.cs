using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon;

/// <summary>
/// EF Core context for the SQLite store. Schema mirrors the sketch's tables;
/// hot-query columns are indexed; <c>RawTags</c> preserves the full ADR-0018
/// tag bag so new tags don't force a migration.
/// </summary>
public sealed class BeaconDbContext(DbContextOptions<BeaconDbContext> options) : DbContext(options)
{
    public DbSet<Build> Builds => Set<Build>();
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Build>(b =>
        {
            b.ToTable("builds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Seq).HasColumnName("seq");
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
            b.Property(x => x.RawTags).HasColumnName("raw_tags").IsRequired();

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
            b.Property(x => x.RawTags).HasColumnName("raw_tags").IsRequired();

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
            b.Property(x => x.RawTags).HasColumnName("raw_tags").IsRequired();

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
            b.Property(x => x.RawTags).HasColumnName("raw_tags").IsRequired();

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
}
