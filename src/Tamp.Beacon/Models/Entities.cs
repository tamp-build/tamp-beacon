using System;
using System.Collections.Generic;

namespace Tamp.Beacon.Models;

/// <summary>
/// One row per build (per <c>TampBuild.Execute&lt;T&gt;</c> invocation).
/// Columns are hand-mapped from the ADR-0018 build-span tag set; the full
/// tag dictionary is preserved in <see cref="RawTags"/> JSON so new tags
/// flow through without a schema migration.
/// </summary>
public sealed class Build
{
    public long Id { get; set; }

    /// <summary>Monotonic sequence used by dashboard delta polling.</summary>
    public long Seq { get; set; }

    /// <summary>
    /// FK to <see cref="Project"/> — denormalized for the cross-project list
    /// query path. Source of truth is <see cref="BuildConfig.ProjectId"/>;
    /// the receiver writes both at ingest time so they can't drift.
    /// </summary>
    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>
    /// Authoritative routing FK — every Build belongs to a BuildConfig (a
    /// pipeline like <c>main-ci</c> or <c>nightly</c>). Resolved at ingest
    /// from the <c>tamp.build.config.name</c> tag on the Build span, falling
    /// back to <c>default</c> when the tag is absent.
    /// </summary>
    public long BuildConfigId { get; set; }
    public BuildConfig BuildConfig { get; set; } = null!;

    public string ProjectName { get; set; } = "unknown";
    public string? ProjectArea { get; set; }

    /// <summary>
    /// Top-level grouping above project — the unit a tree-view dashboard rolls up.
    /// Sourced from <c>tamp.build.organization</c> tag (and forthcoming
    /// <c>[BuildProject(Organization=...)]</c> attribute in Tamp.Core 1.4.1).
    /// Falls back to <see cref="ProjectName"/> when unset so single-product setups
    /// just show a single org node with that project under it.
    /// </summary>
    public string Organization { get; set; } = "unknown";
    public string? CliVersion { get; set; }
    public long StartedUnixNs { get; set; }
    public long DurationNs { get; set; }
    public int ExitCode { get; set; }

    /// <summary>One of <c>success</c> / <c>failure</c> per ADR-0018 outcome vocabulary.</summary>
    public string Outcome { get; set; } = "success";

    public int TargetsTotal { get; set; }
    public int TargetsFailed { get; set; }
    public int CommandsTotal { get; set; }
    public string? FailureTarget { get; set; }
    public string? HostOs { get; set; }
    public string? HostArch { get; set; }
    public string? CiVendor { get; set; }
    public long PeakMemoryBytes { get; set; }

    /// <summary>JSON serialization of the full ADR-0018 tag bag for this span.</summary>
    public string RawTags { get; set; } = "{}";

    public ICollection<Target> Targets { get; set; } = new List<Target>();
    public ICollection<Event> Events { get; set; } = new List<Event>();
}

public sealed class Target
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public Build Build { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? Phase { get; set; }

    /// <summary>One of <c>success</c> / <c>failure</c> / <c>skipped</c> / <c>not_run</c>.</summary>
    public string Status { get; set; } = "success";

    public long StartedUnixNs { get; set; }
    public long DurationNs { get; set; }
    public double CpuTimeMs { get; set; }
    public long GcAllocatedBytes { get; set; }
    public int GcGen0 { get; set; }
    public int GcGen1 { get; set; }
    public int GcGen2 { get; set; }
    public int CommandsCount { get; set; }

    public string RawTags { get; set; } = "{}";

    public ICollection<Command> Commands { get; set; } = new List<Command>();
}

public sealed class Command
{
    public long Id { get; set; }
    public long TargetId { get; set; }
    public Target Target { get; set; } = null!;

    public string Executable { get; set; } = "";
    public int ArgsCount { get; set; }
    public int ExitCode { get; set; }
    public long DurationNs { get; set; }
    public double CpuTotalMs { get; set; }
    public long PeakMemoryBytes { get; set; }
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }

    public string RawTags { get; set; } = "{}";
}

public sealed class Event
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public Build Build { get; set; } = null!;

    public long? TargetId { get; set; }
    public Target? Target { get; set; }

    public long? CommandId { get; set; }
    public Command? Command { get; set; }

    /// <summary>e.g. <c>tamp.build.summary</c>, <c>tamp.target.retry.attempt</c>.</summary>
    public string Name { get; set; } = "";
    public long AtUnixNs { get; set; }

    public string RawTags { get; set; } = "{}";
}

public sealed class PushSubscription
{
    public long Id { get; set; }

    /// <summary>RFC 8030 endpoint URL — unique per device subscription.</summary>
    public string Endpoint { get; set; } = "";

    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";

    /// <summary>
    /// User who owns this subscription. Subscriptions are deleted with
    /// their owner via cascade — disabled users stop receiving alerts.
    /// </summary>
    public long UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Project the subscription is scoped to. Cascade on project archive
    /// keeps the table clean when projects retire.
    /// </summary>
    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public long CreatedUnixNs { get; set; }
}
