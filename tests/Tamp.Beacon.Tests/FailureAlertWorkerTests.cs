using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Tamp.Beacon.Models;
using Tamp.Beacon.Push;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Integration tests for <see cref="FailureAlertWorker"/>. Substitutes
/// <see cref="IWebPushSender"/> with a recording fake so we can assert
/// who got notified without standing up a real Web Push transport.
/// </summary>
public sealed class FailureAlertWorkerTests : IClassFixture<FailureAlertWorkerFixture>
{
    private readonly FailureAlertWorkerFixture _fx;
    public FailureAlertWorkerTests(FailureAlertWorkerFixture fx) => _fx = fx;

    [Fact]
    public async Task Worker_Sends_Notification_For_New_Failure_To_Subscribed_Members()
    {
        await _fx.EnsureAdminAsync("scott", "correct-horse-battery-staple");
        using var admin = await _fx.LoginAsAsync("scott", "correct-horse-battery-staple");

        var slug = $"wn-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "wn");

        // Subscribe a (test) push endpoint via the API so the FK + RBAC are real.
        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";
        var subResp = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPx", auth = "AA" },
        });
        subResp.EnsureSuccessStatusCode();

        FakeWebPushSender.Reset();

        // Seed a failure build directly.
        var buildId = await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: slug);

        // Wait for the worker (default 1s scan interval) to pick it up.
        var seen = await WaitForRecordedAsync(
            predicate: r => r.Subscription.ProjectId == pid && r.Subscription.Endpoint == endpoint,
            timeout: TimeSpan.FromSeconds(8));
        Assert.True(seen, "worker did not record a notification for the seeded failure within the timeout");

        // Worker should have looked up the build by ProjectId and emitted exactly one notification.
        var calls = FakeWebPushSender.Calls.Where(c => c.Subscription.ProjectId == pid).ToList();
        Assert.NotEmpty(calls);
        Assert.Contains(buildId.ToString(), System.Text.Json.JsonSerializer.Serialize(calls[0].Payload));
    }

    [Fact]
    public async Task Worker_Coalesces_Repeat_Failures_For_Same_Project_Target()
    {
        await _fx.EnsureAdminAsync("scott", "correct-horse-battery-staple");
        using var admin = await _fx.LoginAsAsync("scott", "correct-horse-battery-staple");

        var slug = $"co-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "co");

        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPx", auth = "AA" },
        });

        FakeWebPushSender.Reset();

        // Seed two failures back-to-back on the same target — coalescer must
        // suppress the second within the 1-hour window the fixture configures.
        await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: slug);
        await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: slug);

        // Settle.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var calls = FakeWebPushSender.Calls.Where(c => c.Subscription.ProjectId == pid).ToList();
        Assert.Single(calls);
    }

    [Fact]
    public async Task Worker_Skips_Success_Builds()
    {
        await _fx.EnsureAdminAsync("scott", "correct-horse-battery-staple");
        using var admin = await _fx.LoginAsAsync("scott", "correct-horse-battery-staple");

        var slug = $"sk-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "sk");
        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPx", auth = "AA" },
        });

        FakeWebPushSender.Reset();
        await _fx.SeedBuildAsync(pid, outcome: "success", projectName: slug);

        await Task.Delay(TimeSpan.FromSeconds(3));
        var calls = FakeWebPushSender.Calls.Where(c => c.Subscription.ProjectId == pid).ToList();
        Assert.Empty(calls);
    }

    private static async Task<bool> WaitForRecordedAsync(
        Func<FakeWebPushSender.RecordedCall, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (FakeWebPushSender.Calls.Any(predicate)) return true;
            await Task.Delay(200);
        }
        return false;
    }
}

/// <summary>
/// Fixture variant that swaps <see cref="IWebPushSender"/> for the
/// recording fake AND tightens the worker scan interval so tests don't
/// hang on the default 1s loop.
/// </summary>
public sealed class FailureAlertWorkerFixture : BeaconAppFixture
{
    public FailureAlertWorkerFixture()
    {
        ConfigOverrides["Beacon:FailureWorkerIntervalMs"] = "100";
        // Generous coalesce window so the repeat-suppression test is
        // deterministic without sleeping a real 5-minute window.
        ConfigOverrides["Beacon:FailureAlertWindowSeconds"] = "3600";
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        // Replace the live HTTPS sender with the recording fake so we can
        // assert which subscriptions got notified.
        services.AddScoped<IWebPushSender, FakeWebPushSender>();
    }
}

internal sealed class FakeWebPushSender : IWebPushSender
{
    public sealed record RecordedCall(Tamp.Beacon.Models.PushSubscription Subscription, object Payload);

    private static readonly ConcurrentQueue<RecordedCall> _calls = new();

    public static System.Collections.Generic.IReadOnlyList<RecordedCall> Calls => _calls.ToArray();

    public static void Reset()
    {
        while (_calls.TryDequeue(out _)) { }
    }

    public Task<bool> SendAsync(Tamp.Beacon.Models.PushSubscription sub, object payload, CancellationToken ct = default)
    {
        _calls.Enqueue(new RecordedCall(sub, payload));
        return Task.FromResult(true);
    }
}
