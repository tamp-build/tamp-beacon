using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Tamp.Beacon;
using Xunit;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ExportTraceRequest = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest;

namespace Tamp.Beacon.Tests;

/// <summary>
/// TAM-218 regression — Target spans arriving in OTLP batches BEFORE
/// their parent Build span (the common case for any build longer than
/// the exporter's scheduled-flush interval) must reconcile to a single
/// Build row by traceId, not scatter across synthetic Build rows.
/// </summary>
public sealed class OtlpCrossBatchReconcileTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";

    public OtlpCrossBatchReconcileTests(BeaconAppFixture fx) => _fx = fx;

    private async Task<(string slug, string token)> CreateProjectAndTokenAsync(System.Net.Http.HttpClient admin, string prefix)
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(15, prefix.Length + 9));
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = slug });
        var mint = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "ci" });
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>();
        return (slug, body.GetProperty("token").GetString()!);
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static ExportTraceRequest TargetOnlyRequest(string traceIdHex, string spanIdHex, string targetName)
    {
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(HexToBytes(traceIdHex)),
            SpanId = ByteString.CopyFrom(HexToBytes(spanIdHex)),
            Name = $"target:{targetName}",
            Kind = OtlpSpan.Types.SpanKind.Internal,
            StartTimeUnixNano = 1_700_000_000_000_000_000UL,
            EndTimeUnixNano = 1_700_000_001_000_000_000UL,
        };
        span.Attributes.Add(new KeyValue { Key = "tamp.target.name", Value = new AnyValue { StringValue = targetName } });
        span.Attributes.Add(new KeyValue { Key = "tamp.target.status", Value = new AnyValue { StringValue = "success" } });

        return new ExportTraceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "Tamp.Build.Targets", Version = "1.9.0" },
                            Spans = { span },
                        },
                    },
                },
            },
        };
    }

    private static ExportTraceRequest BuildOnlyRequest(string traceIdHex, string spanIdHex, string projectName)
    {
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(HexToBytes(traceIdHex)),
            SpanId = ByteString.CopyFrom(HexToBytes(spanIdHex)),
            Name = "build",
            Kind = OtlpSpan.Types.SpanKind.Internal,
            StartTimeUnixNano = 1_700_000_000_000_000_000UL,
            EndTimeUnixNano = 1_700_000_005_000_000_000UL,
        };
        span.Attributes.Add(new KeyValue { Key = "tamp.build.project.name", Value = new AnyValue { StringValue = projectName } });
        span.Attributes.Add(new KeyValue { Key = "tamp.build.targets.total", Value = new AnyValue { IntValue = 5 } });
        span.Attributes.Add(new KeyValue { Key = "tamp.build.exit_code", Value = new AnyValue { IntValue = 0 } });
        span.Attributes.Add(new KeyValue { Key = "outcome", Value = new AnyValue { StringValue = "success" } });

        return new ExportTraceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "Tamp.Build", Version = "1.9.0" },
                            Spans = { span },
                        },
                    },
                },
            },
        };
    }

    private async Task<HttpResponseMessage> PostProtoAsync(string token, IMessage req)
    {
        using var client = _fx.Factory.CreateClient();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(req.ToByteArray()),
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(msg);
    }

    [Fact]
    public async Task Targets_Arriving_Before_Build_Reconcile_To_One_Build_When_Build_Lands()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token) = await CreateProjectAndTokenAsync(admin, "rec");

        var trace = Guid.NewGuid().ToString("N");
        var buildSpan = Guid.NewGuid().ToString("N").Substring(0, 16);
        var restoreSpan = Guid.NewGuid().ToString("N").Substring(0, 16);
        var compileSpan = Guid.NewGuid().ToString("N").Substring(0, 16);

        // Batch 1 — Target spans arrive WITHOUT a parent Build span.
        // Pre-fix: creates a synthetic Build for `Restore`, attaches Compile to it
        // (in-batch dedupe by traceId works). Post-fix: same, since no DB hit yet.
        var batch1 = TargetOnlyRequest(trace, restoreSpan, "Restore");
        batch1.ResourceSpans[0].ScopeSpans[0].Spans.Add(
            TargetOnlyRequest(trace, compileSpan, "Compile").ResourceSpans[0].ScopeSpans[0].Spans[0]);
        Assert.Equal(HttpStatusCode.OK, (await PostProtoAsync(token, batch1)).StatusCode);

        // Batch 2 — the Build span lands. Pre-fix: a NEW Build row gets
        // inserted, leaving the synthetic from batch 1 orphaned. Post-fix:
        // the receiver finds the synthetic by (project, trace_id) and
        // OVERLAYS this span's rich attrs onto the same row.
        var batch2 = BuildOnlyRequest(trace, buildSpan, slug);
        Assert.Equal(HttpStatusCode.OK, (await PostProtoAsync(token, batch2)).StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Slug == slug);

        var buildsForProject = await db.Builds.AsNoTracking()
            .Where(b => b.ProjectId == project.Id)
            .ToListAsync();
        // The fix: exactly one Build row reconciled by trace_id.
        Assert.Single(buildsForProject);

        var build = buildsForProject[0];
        Assert.Equal(trace, build.TraceId);
        Assert.Equal(5, build.TargetsTotal);       // overlayed from the Build-span attrs
        Assert.Equal("success", build.Outcome);

        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.BuildId == build.Id)
            .Select(t => t.Name)
            .ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.Contains("Restore", targets);
        Assert.Contains("Compile", targets);
    }

    [Fact]
    public async Task Targets_Arriving_After_Build_Attach_To_Same_Build_Across_Batches()
    {
        // Less common shape — Build span lands first (small builds where
        // every batch is the final Dispose flush). Subsequent Target spans
        // for the same trace in a LATER batch still need to attach.
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token) = await CreateProjectAndTokenAsync(admin, "aft");

        var trace = Guid.NewGuid().ToString("N");
        var buildSpan = Guid.NewGuid().ToString("N").Substring(0, 16);
        var testSpan = Guid.NewGuid().ToString("N").Substring(0, 16);

        Assert.Equal(HttpStatusCode.OK,
            (await PostProtoAsync(token, BuildOnlyRequest(trace, buildSpan, slug))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PostProtoAsync(token, TargetOnlyRequest(trace, testSpan, "Test"))).StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Slug == slug);
        var buildsForProject = await db.Builds.AsNoTracking()
            .Where(b => b.ProjectId == project.Id)
            .ToListAsync();
        Assert.Single(buildsForProject);
        Assert.Equal(trace, buildsForProject[0].TraceId);

        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.BuildId == buildsForProject[0].Id)
            .Select(t => t.Name)
            .ToListAsync();
        Assert.Equal(new[] { "Test" }, targets);
    }
}
