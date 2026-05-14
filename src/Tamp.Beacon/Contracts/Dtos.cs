using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Tamp.Beacon.Contracts;

/// <summary>
/// DTOs for the tamp-beacon HTTP/JSON API. Stable shape — adding fields is
/// non-breaking, renaming is a 1.0 promise we haven't made yet so we may
/// rev in the 0.x line. Properties match the JSON snake_case names via
/// <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
public sealed record BuildSummary
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    /// <summary>Authoritative project routing key (FK slug). Stable across renames.</summary>
    [JsonPropertyName("project_slug")] public string ProjectSlug { get; init; } = "";
    /// <summary>Project display name at ingest time (denormalized hint; may drift if project renamed).</summary>
    [JsonPropertyName("organization")] public string Organization { get; init; } = "";
    [JsonPropertyName("project_name")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("project_area")] public string? ProjectArea { get; init; }
    [JsonPropertyName("cli_version")] public string? CliVersion { get; init; }
    [JsonPropertyName("started_unix_ns")] public long StartedUnixNs { get; init; }
    [JsonPropertyName("duration_ns")] public long DurationNs { get; init; }
    [JsonPropertyName("exit_code")] public int ExitCode { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
    [JsonPropertyName("targets_total")] public int TargetsTotal { get; init; }
    [JsonPropertyName("targets_failed")] public int TargetsFailed { get; init; }
    [JsonPropertyName("commands_total")] public int CommandsTotal { get; init; }
    [JsonPropertyName("failure_target")] public string? FailureTarget { get; init; }
    [JsonPropertyName("host_os")] public string? HostOs { get; init; }
    [JsonPropertyName("host_arch")] public string? HostArch { get; init; }
    [JsonPropertyName("ci_vendor")] public string? CiVendor { get; init; }
    [JsonPropertyName("peak_memory_b")] public long PeakMemoryBytes { get; init; }
}

public sealed record TargetSummary
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("build_id")] public long BuildId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("started_unix_ns")] public long StartedUnixNs { get; init; }
    [JsonPropertyName("duration_ns")] public long DurationNs { get; init; }
    [JsonPropertyName("cpu_time_ms")] public double CpuTimeMs { get; init; }
    [JsonPropertyName("gc_allocated_b")] public long GcAllocatedBytes { get; init; }
    [JsonPropertyName("gc_gen0")] public int GcGen0 { get; init; }
    [JsonPropertyName("gc_gen1")] public int GcGen1 { get; init; }
    [JsonPropertyName("gc_gen2")] public int GcGen2 { get; init; }
    [JsonPropertyName("commands_count")] public int CommandsCount { get; init; }
}

public sealed record CommandSummary
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("target_id")] public long TargetId { get; init; }
    [JsonPropertyName("executable")] public string Executable { get; init; } = "";
    [JsonPropertyName("args_count")] public int ArgsCount { get; init; }
    [JsonPropertyName("exit_code")] public int ExitCode { get; init; }
    [JsonPropertyName("duration_ns")] public long DurationNs { get; init; }
    [JsonPropertyName("cpu_total_ms")] public double CpuTotalMs { get; init; }
    [JsonPropertyName("peak_memory_b")] public long PeakMemoryBytes { get; init; }
    [JsonPropertyName("stdout_bytes")] public long StdoutBytes { get; init; }
    [JsonPropertyName("stderr_bytes")] public long StderrBytes { get; init; }
}

public sealed record EventSummary
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("build_id")] public long BuildId { get; init; }
    [JsonPropertyName("target_id")] public long? TargetId { get; init; }
    [JsonPropertyName("command_id")] public long? CommandId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("at_unix_ns")] public long AtUnixNs { get; init; }
}

public sealed record BuildList
{
    [JsonPropertyName("builds")] public IReadOnlyList<BuildSummary> Builds { get; init; } = Array.Empty<BuildSummary>();
    [JsonPropertyName("next_seq")] public long NextSeq { get; init; }
}

public sealed record BuildDetail
{
    [JsonPropertyName("build")] public BuildSummary Build { get; init; } = new();
    [JsonPropertyName("targets")] public IReadOnlyList<TargetSummary> Targets { get; init; } = Array.Empty<TargetSummary>();
    [JsonPropertyName("commands")] public IReadOnlyList<CommandSummary> Commands { get; init; } = Array.Empty<CommandSummary>();
    [JsonPropertyName("events")] public IReadOnlyList<EventSummary> Events { get; init; } = Array.Empty<EventSummary>();
}

public sealed record ProjectFacet
{
    [JsonPropertyName("organization")] public string Organization { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("area")] public string? Area { get; init; }
    [JsonPropertyName("last_seen_unix_ns")] public long LastSeenUnixNs { get; init; }
    [JsonPropertyName("builds_count")] public long BuildsCount { get; init; }
    [JsonPropertyName("failed_count")] public long FailedCount { get; init; }
}

public sealed record ProjectList
{
    [JsonPropertyName("projects")] public IReadOnlyList<ProjectFacet> Projects { get; init; } = Array.Empty<ProjectFacet>();
}

public sealed record OrganizationFacet
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("projects_count")] public int ProjectsCount { get; init; }
    [JsonPropertyName("builds_count")] public long BuildsCount { get; init; }
    [JsonPropertyName("failed_count")] public long FailedCount { get; init; }
    [JsonPropertyName("last_seen_unix_ns")] public long LastSeenUnixNs { get; init; }
}

public sealed record OrganizationList
{
    [JsonPropertyName("organizations")] public IReadOnlyList<OrganizationFacet> Organizations { get; init; } = Array.Empty<OrganizationFacet>();
}

public sealed record TargetStat
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("project_name")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("avg_duration_ns")] public double AvgDurationNs { get; init; }
    [JsonPropertyName("p95_duration_ns")] public double P95DurationNs { get; init; }
    [JsonPropertyName("samples")] public int Samples { get; init; }
}

public sealed record TargetStatList
{
    [JsonPropertyName("targets")] public IReadOnlyList<TargetStat> Targets { get; init; } = Array.Empty<TargetStat>();
}

public sealed record FlakyTarget
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("project_name")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("fail_rate")] public double FailRate { get; init; }
    [JsonPropertyName("samples")] public int Samples { get; init; }
}

public sealed record FlakyTargetList
{
    [JsonPropertyName("targets")] public IReadOnlyList<FlakyTarget> Targets { get; init; } = Array.Empty<FlakyTarget>();
}

public sealed record PushSubscriptionRequest
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
    [JsonPropertyName("keys")] public PushKeys Keys { get; init; } = new();
    [JsonPropertyName("project_filter")] public string? ProjectFilter { get; init; }
    [JsonPropertyName("area_filter")] public string? AreaFilter { get; init; }
}

public sealed record PushKeys
{
    [JsonPropertyName("p256dh")] public string P256dh { get; init; } = "";
    [JsonPropertyName("auth")] public string Auth { get; init; } = "";
}
