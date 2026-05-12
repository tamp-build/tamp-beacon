using System;
using System.Collections.Generic;
using Tamp.Beacon.Otlp;

namespace Tamp.Beacon.Tests.Fixtures;

/// <summary>
/// Hand-built OTLP/JSON fixtures matching the ADR-0018 tag contract. These
/// are deliberately verbose so a future tag rename trips tests cleanly.
/// </summary>
internal static class OtlpFixtures
{
    public static ExportTraceServiceRequest TrivialSuccess(string project = "HoldFast", string? area = "frontend")
    {
        var buildSpanId = "0011223344556677";
        var traceId = "00112233445566778899aabbccddeeff";

        return new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = new Resource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = OtlpTraceReceiver.BuildSource, Version = "1.4.0" },
                            Spans =
                            {
                                new Span
                                {
                                    TraceId = traceId,
                                    SpanId = buildSpanId,
                                    Name = "build",
                                    StartTimeUnixNano = "1700000000000000000",
                                    EndTimeUnixNano   = "1700000005000000000",
                                    Attributes = BuildAttributes(project, area),
                                    Events =
                                    {
                                        new SpanEvent
                                        {
                                            Name = "tamp.build.summary",
                                            TimeUnixNano = "1700000005000000000",
                                            Attributes = Kvs(
                                                ("outcome", "success"),
                                                ("tamp.build.targets.total", 3L),
                                                ("tamp.build.targets.failed", 0L)),
                                        },
                                    },
                                },
                            },
                        },
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = OtlpTraceReceiver.TargetSource, Version = "1.4.0" },
                            Spans =
                            {
                                MakeTargetSpan(traceId, buildSpanId, "1111000000000001", "Compile"),
                                MakeTargetSpan(traceId, buildSpanId, "1111000000000002", "Test"),
                                MakeTargetSpan(traceId, buildSpanId, "1111000000000003", "Pack"),
                            },
                        },
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = OtlpTraceReceiver.CommandSource, Version = "1.4.0" },
                            Spans =
                            {
                                MakeCommandSpan(traceId, "1111000000000001", "2222000000000001", "dotnet"),
                                MakeCommandSpan(traceId, "1111000000000002", "2222000000000002", "dotnet"),
                            },
                        },
                    },
                },
            },
        };
    }

    public static ExportTraceServiceRequest TrivialFailure(string project, string failingTarget = "Test")
    {
        var req = TrivialSuccess(project, area: null);
        var build = req.ResourceSpans[0].ScopeSpans[0].Spans[0];
        // Flip the outcome and exit code by replacing the outcome+exit-code tags.
        SetTag(build.Attributes, "outcome", "failure");
        SetTag(build.Attributes, "tamp.build.exit_code", 1L);
        SetTag(build.Attributes, "tamp.build.failure.target", failingTarget);
        SetTag(build.Attributes, "tamp.build.targets.failed", 1L);

        foreach (var t in req.ResourceSpans[0].ScopeSpans[1].Spans)
        {
            if (string.Equals(GetTag(t.Attributes, "tamp.target.name"), failingTarget, StringComparison.Ordinal))
                SetTag(t.Attributes, "tamp.target.status", "failure");
        }
        return req;
    }

    /// <summary>A non-Tamp source name — beacon should reject this with 422.</summary>
    public static ExportTraceServiceRequest NonTampPayload()
    {
        return new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = new Resource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "SomeOtherApp.Workflow", Version = "1.0" },
                            Spans = { new Span { TraceId = "deadbeef", SpanId = "feedface", Name = "noop" } },
                        },
                    },
                },
            },
        };
    }

    private static Span MakeTargetSpan(string traceId, string parentSpanId, string spanId, string name)
        => new()
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Name = $"target:{name}",
            StartTimeUnixNano = "1700000000100000000",
            EndTimeUnixNano = "1700000001500000000",
            Attributes = Kvs(
                ("tamp.target.name", name),
                ("tamp.target.status", "success"),
                ("tamp.target.phase", "Build"),
                ("tamp.target.duration_ns", 1_400_000_000L),
                ("tamp.target.cpu_time_ms", 1234.5),
                ("tamp.target.gc_allocated_bytes", 12_345_678L),
                ("tamp.target.gc.gen0.collections", 3L),
                ("tamp.target.gc.gen1.collections", 1L),
                ("tamp.target.gc.gen2.collections", 0L),
                ("tamp.target.commands.count", 1L)),
        };

    private static Span MakeCommandSpan(string traceId, string parentSpanId, string spanId, string exe)
        => new()
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Name = $"command:{exe}",
            StartTimeUnixNano = "1700000000200000000",
            EndTimeUnixNano = "1700000001400000000",
            Attributes = Kvs(
                ("tamp.cmd.executable", exe),
                ("tamp.cmd.args.count", 4L),
                ("tamp.cmd.exit_code", 0L),
                ("tamp.cmd.duration_ns", 1_200_000_000L),
                ("tamp.cmd.stdout_bytes", 4096L),
                ("tamp.cmd.stderr_bytes", 0L),
                ("outcome", "success")),
        };

    private static List<KeyValue> BuildAttributes(string project, string? area) => Kvs(
        ("tamp.build.project.name", project),
        ("tamp.build.project.area", (object?)area),
        ("tamp.build.cli_version", "1.4.0"),
        ("tamp.build.exit_code", 0L),
        ("tamp.build.duration_ns", 5_000_000_000L),
        ("tamp.build.peak_working_set_bytes", 256L * 1024L * 1024L),
        ("tamp.build.targets.total", 3L),
        ("tamp.build.targets.failed", 0L),
        ("tamp.build.commands.total", 2L),
        ("tamp.host.os", "linux"),
        ("tamp.host.arch", "X64"),
        ("tamp.ci.vendor", "github-actions"),
        ("outcome", "success"));

    private static List<KeyValue> Kvs(params (string Key, object? Value)[] pairs)
    {
        var list = new List<KeyValue>(pairs.Length);
        foreach (var (k, v) in pairs)
        {
            if (v is null) continue;
            list.Add(new KeyValue { Key = k, Value = WrapValue(v) });
        }
        return list;
    }

    private static AnyValue WrapValue(object value) => value switch
    {
        string s => new AnyValue { StringValue = s },
        bool b => new AnyValue { BoolValue = b },
        long l => new AnyValue { IntValue = l.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        int i => new AnyValue { IntValue = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        double d => new AnyValue { DoubleValue = d },
        _ => new AnyValue { StringValue = value.ToString() ?? "" },
    };

    private static void SetTag(List<KeyValue> attrs, string key, object? value)
    {
        if (value is null) return;
        var existing = attrs.Find(a => a.Key == key);
        if (existing is null) attrs.Add(new KeyValue { Key = key, Value = WrapValue(value) });
        else existing.Value = WrapValue(value);
    }

    private static string? GetTag(List<KeyValue> attrs, string key)
        => attrs.Find(a => a.Key == key)?.Value?.StringValue;
}
