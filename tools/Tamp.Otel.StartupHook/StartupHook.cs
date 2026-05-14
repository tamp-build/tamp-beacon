// .NET runtime startup hook contract: type named StartupHook in the global namespace,
// public static void Initialize() entry. Loaded via DOTNET_STARTUP_HOOKS before user Main runs.
//
// This hook is the seed of the eventual Tamp.Otel satellite (TAM-129). For now it lives
// in tamp-beacon's tools/ folder as the load-gen driver's instrumentation harness:
//   DOTNET_STARTUP_HOOKS=/path/to/Tamp.Otel.StartupHook.dll \
//   OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318       \
//   dotnet run --project build/Build.csproj -- Compile
//
// Subscribes to the ADR 0018 ActivitySources (Tamp.Build, Tamp.Build.Targets,
// Tamp.Build.Commands), buffers every Activity, and on process exit POSTs them as an
// OTLP/HTTP-JSON ExportTraceServiceRequest to beacon's /v1/traces endpoint.
//
// Pure BCL: no NuGet dependencies. Custom JSON serialization matches the OTLP/HTTP-JSON
// shape that tamp-beacon's OtlpTraceReceiver already parses (validated by the sample
// payload smoke test). When Tamp.Otel ships properly, this gets swapped for the standard
// OpenTelemetry SDK's OtlpExporter (HttpProtobuf) + beacon learns protobuf — both better
// long-term, but JSON is the fastest path to real telemetry RIGHT NOW.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal class StartupHook
{
    private static readonly ConcurrentBag<Activity> _activities = [];
    private static string? _endpoint;
    private static string? _serviceName;
    private static string? _serviceArea;
    private static string? _organization;       // Top-level grouping above project. Forthcoming as BuildProject(Organization=...) in Tamp.Core 1.4.1.
    private static readonly object _shutdownLock = new();
    private static bool _shutdownFired;

    public static void Initialize()
    {
        _endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            // No endpoint configured — hook is a no-op. Allows the same DLL to be safely
            // left enabled in CI workflows where beacon isn't reachable (Scott's gating).
            return;
        }

        _serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        _serviceArea = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAMESPACE");
        _organization = Environment.GetEnvironmentVariable("OTEL_BUILD_ORGANIZATION");

        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("Tamp.Build", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _activities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        // Flush on graceful exit. ProcessExit fires on Ctrl+C, normal Main return,
        // and SIGTERM (in .NET 10). AssemblyLoadContext.Default.Unloading is a backup
        // for managed-host shutdowns.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ => Shutdown();
    }

    private static void Shutdown()
    {
        lock (_shutdownLock)
        {
            if (_shutdownFired) return;
            _shutdownFired = true;
        }
        try { Flush().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            // Telemetry must never crash the host process. Surface to stderr for debugging.
            try { Console.Error.WriteLine($"[Tamp.Otel.StartupHook] flush failed: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* if even stderr is gone, give up silently */ }
        }
    }

    private static async Task Flush()
    {
        if (string.IsNullOrEmpty(_endpoint)) return;

        var snapshot = _activities.ToArray();
        if (snapshot.Length == 0) return;

        var payload = BuildOtlpJson(snapshot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var url = _endpoint.TrimEnd('/') + "/v1/traces";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.Error.WriteLine($"[Tamp.Otel.StartupHook] beacon rejected payload ({(int)response.StatusCode}): {body}");
        }
    }

    private static string BuildOtlpJson(Activity[] activities)
    {
        // Group activities by source name → one scopeSpans per source. OTLP requires
        // resource + scopeSpans + spans nesting; we ship a single resource per process.
        var bySource = activities.GroupBy(a => a.Source.Name).ToList();

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteStartArray("resourceSpans");
        writer.WriteStartObject();

        // Resource attributes — service.name + namespace correlate with the
        // BuildProject attribute that ADR 0018 documents. Adopters set these
        // explicitly to override what the build span tags.
        writer.WriteStartObject("resource");
        writer.WriteStartArray("attributes");
        if (!string.IsNullOrEmpty(_serviceName)) WriteKv(writer, "service.name", _serviceName);
        if (!string.IsNullOrEmpty(_serviceArea)) WriteKv(writer, "service.namespace", _serviceArea);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("scopeSpans");
        foreach (var group in bySource)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("scope");
            writer.WriteString("name", group.Key);
            var version = group.First().Source.Version;
            if (!string.IsNullOrEmpty(version)) writer.WriteString("version", version);
            writer.WriteEndObject();

            writer.WriteStartArray("spans");
            foreach (var activity in group)
            {
                WriteSpan(writer, activity);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();   // scopeSpans

        writer.WriteEndObject();
        writer.WriteEndArray();   // resourceSpans
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSpan(Utf8JsonWriter writer, Activity activity)
    {
        writer.WriteStartObject();
        writer.WriteString("traceId", activity.TraceId.ToHexString());
        writer.WriteString("spanId", activity.SpanId.ToHexString());
        if (activity.ParentSpanId != default)
            writer.WriteString("parentSpanId", activity.ParentSpanId.ToHexString());
        writer.WriteString("name", activity.OperationName);
        writer.WriteNumber("kind", (int)activity.Kind switch
        {
            // OTLP span kind enum: 0=UNSPECIFIED, 1=INTERNAL, 2=SERVER, 3=CLIENT, 4=PRODUCER, 5=CONSUMER
            // .NET ActivityKind: 0=Internal, 1=Server, 2=Client, 3=Producer, 4=Consumer
            // Translate to OTLP numbering.
            (int)ActivityKind.Internal => 1,
            (int)ActivityKind.Server   => 2,
            (int)ActivityKind.Client   => 3,
            (int)ActivityKind.Producer => 4,
            (int)ActivityKind.Consumer => 5,
            _                          => 0,
        });
        // Times: OTLP wants unix-nano strings (JSON numbers are limited to 2^53; nanos exceed).
        writer.WriteString("startTimeUnixNano", ToUnixNanos(activity.StartTimeUtc).ToString());
        writer.WriteString("endTimeUnixNano", ToUnixNanos(activity.StartTimeUtc + activity.Duration).ToString());

        // Attributes — inject the organization tag on the root build span so
        // beacon's receiver can populate the new `organization` column without
        // requiring a Tamp.Core API change in this iteration.
        writer.WriteStartArray("attributes");
        var emittedOrgTag = false;
        foreach (var (key, value) in activity.TagObjects)
        {
            if (value is null) continue;
            WriteKv(writer, key, value);
            if (key == "tamp.build.organization") emittedOrgTag = true;
        }
        if (!emittedOrgTag
            && !string.IsNullOrEmpty(_organization)
            && activity.Source.Name == "Tamp.Build"
            && activity.OperationName == "build")
        {
            WriteKv(writer, "tamp.build.organization", _organization);
        }
        writer.WriteEndArray();

        // Events
        if (activity.Events.Any())
        {
            writer.WriteStartArray("events");
            foreach (var ev in activity.Events)
            {
                writer.WriteStartObject();
                writer.WriteString("name", ev.Name);
                writer.WriteString("timeUnixNano", ToUnixNanos(ev.Timestamp.UtcDateTime).ToString());
                writer.WriteStartArray("attributes");
                foreach (var (key, value) in ev.Tags)
                {
                    if (value is null) continue;
                    WriteKv(writer, key, value);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        // Status
        writer.WriteStartObject("status");
        writer.WriteNumber("code", activity.Status switch
        {
            ActivityStatusCode.Ok    => 1,
            ActivityStatusCode.Error => 2,
            _                        => 0,
        });
        if (!string.IsNullOrEmpty(activity.StatusDescription))
            writer.WriteString("message", activity.StatusDescription);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteKv(Utf8JsonWriter writer, string key, object value)
    {
        writer.WriteStartObject();
        writer.WriteString("key", key);
        writer.WriteStartObject("value");
        switch (value)
        {
            case string s:
                writer.WriteString("stringValue", s);
                break;
            case bool b:
                writer.WriteBoolean("boolValue", b);
                break;
            case int i:
                writer.WriteString("intValue", i.ToString());      // OTLP intValue is string-encoded int64
                break;
            case long l:
                writer.WriteString("intValue", l.ToString());
                break;
            case double d:
                writer.WriteNumber("doubleValue", d);
                break;
            case float f:
                writer.WriteNumber("doubleValue", f);
                break;
            default:
                writer.WriteString("stringValue", value.ToString() ?? "");
                break;
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static long ToUnixNanos(DateTime utc)
    {
        var ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return ticks * 100;  // .NET ticks are 100ns; convert to nanoseconds.
    }
}
