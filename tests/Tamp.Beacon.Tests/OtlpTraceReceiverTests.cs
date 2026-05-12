using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;
using Tamp.Beacon.Otlp;
using Tamp.Beacon.Tests.Fixtures;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class OtlpTraceReceiverTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly BeaconDbContext _db;

    public OtlpTraceReceiverTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new BeaconDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task TrivialSuccess_PersistsBuildTargetsAndCommands()
    {
        var receiver = new OtlpTraceReceiver(_db);
        var req = OtlpFixtures.TrivialSuccess("HoldFast", "frontend");

        var result = await receiver.IngestAsync(req);

        Assert.Equal(1, result.BuildsIngested);
        Assert.Equal(3, result.TargetsIngested);
        Assert.Equal(2, result.CommandsIngested);

        var build = await _db.Builds.SingleAsync();
        Assert.Equal("HoldFast", build.ProjectName);
        Assert.Equal("frontend", build.ProjectArea);
        Assert.Equal("success", build.Outcome);
        Assert.Equal(0, build.ExitCode);
        Assert.Equal(3, build.TargetsTotal);
        Assert.True(build.Seq > 0);

        var targets = await _db.Targets.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(new[] { "Compile", "Test", "Pack" }, targets.Select(t => t.Name).ToArray());
        Assert.All(targets, t => Assert.Equal("success", t.Status));

        var commands = await _db.Commands.ToListAsync();
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal("dotnet", c.Executable));
    }

    [Fact]
    public async Task TrivialSuccess_PersistsBuildEventFromSummary()
    {
        var receiver = new OtlpTraceReceiver(_db);
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess());

        var events = await _db.Events.ToListAsync();
        Assert.Single(events);
        Assert.Equal("tamp.build.summary", events[0].Name);
    }

    [Fact]
    public async Task TrivialFailure_FlipsOutcomeAndCapturesFailingTarget()
    {
        var receiver = new OtlpTraceReceiver(_db);
        var req = OtlpFixtures.TrivialFailure("HoldFast", "Test");
        await receiver.IngestAsync(req);

        var build = await _db.Builds.SingleAsync();
        Assert.Equal("failure", build.Outcome);
        Assert.Equal(1, build.ExitCode);
        Assert.Equal("Test", build.FailureTarget);

        var failing = await _db.Targets.SingleAsync(t => t.Name == "Test");
        Assert.Equal("failure", failing.Status);
    }

    [Fact]
    public async Task NonTampPayload_ThrowsOtlpRejection()
    {
        var receiver = new OtlpTraceReceiver(_db);
        await Assert.ThrowsAsync<OtlpRejectionException>(() =>
            receiver.IngestAsync(OtlpFixtures.NonTampPayload()));
    }

    [Fact]
    public async Task RawTags_PreservesAllAttributes()
    {
        var receiver = new OtlpTraceReceiver(_db);
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess());

        var build = await _db.Builds.SingleAsync();
        Assert.Contains("tamp.build.cli_version", build.RawTags);
        Assert.Contains("tamp.ci.vendor", build.RawTags);
        Assert.Contains("github-actions", build.RawTags);
    }

    [Fact]
    public async Task SequentialIngests_AssignMonotonicSeq()
    {
        var receiver = new OtlpTraceReceiver(_db);
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess("A"));
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess("B"));
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess("C"));

        var seqs = await _db.Builds.OrderBy(b => b.Seq).Select(b => b.Seq).ToListAsync();
        Assert.Equal(3, seqs.Count);
        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);
        Assert.True(seqs[0] < seqs[1] && seqs[1] < seqs[2]);
    }

    [Fact]
    public async Task EmptyPayload_IsNoOp()
    {
        var receiver = new OtlpTraceReceiver(_db);
        var result = await receiver.IngestAsync(new ExportTraceServiceRequest());
        Assert.Equal(0, result.BuildsIngested);
        Assert.Equal(0, await _db.Builds.CountAsync());
    }

    [Fact]
    public async Task NullProjectArea_StoresAsNull()
    {
        var receiver = new OtlpTraceReceiver(_db);
        await receiver.IngestAsync(OtlpFixtures.TrivialSuccess("Solo", area: null));

        var build = await _db.Builds.SingleAsync();
        Assert.Null(build.ProjectArea);
    }

    [Fact]
    public async Task UnicodeProjectName_IsPreserved()
    {
        var receiver = new OtlpTraceReceiver(_db);
        var req = OtlpFixtures.TrivialSuccess("🚀-HoldFast-ümlaut", "frontend");
        await receiver.IngestAsync(req);

        var build = await _db.Builds.SingleAsync();
        Assert.Equal("🚀-HoldFast-ümlaut", build.ProjectName);
    }

    [Fact]
    public void OtlpRejection_CarriesReasonText()
    {
        var ex = new OtlpRejectionException("custom reason");
        Assert.Equal("custom reason", ex.Reason);
        Assert.Equal("custom reason", ex.Message);
    }
}
