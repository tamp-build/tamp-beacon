using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Push;

/// <summary>
/// Background worker that polls the DB for newly-arrived failure builds
/// (by monotonic <c>seq</c>) and pushes a notification to every matching
/// subscription. Matched on <see cref="Models.Build.ProjectId"/> — the
/// auth-derived FK survives project rename. Coalesced by project + target
/// within <see cref="BeaconOptions.FailureAlertWindowSeconds"/>.
/// </summary>
public sealed class FailureAlertWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly FailureCoalescer _coalescer;
    private readonly ILogger<FailureAlertWorker> _logger;
    private readonly BeaconOptions _options;

    private long _lastSeenSeq;

    public FailureAlertWorker(
        IServiceScopeFactory scopes,
        FailureCoalescer coalescer,
        IOptions<BeaconOptions> options,
        ILogger<FailureAlertWorker> logger)
    {
        _scopes = scopes;
        _coalescer = coalescer;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using (var seedScope = _scopes.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
                _lastSeenSeq = await db.Builds.MaxAsync(b => (long?)b.Seq, stoppingToken).ConfigureAwait(false) ?? 0;
            }
            _logger.LogInformation("FailureAlertWorker seeded at seq={Seq}", _lastSeenSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FailureAlertWorker could not seed _lastSeenSeq on startup; will retry");
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(50, _options.FailureWorkerIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FailureAlertWorker scan failed; continuing");
            }
            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IWebPushSender>();

        var newFailures = await db.Builds
            .AsNoTracking()
            .Where(b => b.Seq > _lastSeenSeq && b.Outcome == "failure")
            .OrderBy(b => b.Seq)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var maxSeqThisScan = await db.Builds.AsNoTracking()
            .MaxAsync(b => (long?)b.Seq, ct)
            .ConfigureAwait(false) ?? _lastSeenSeq;

        foreach (var build in newFailures)
        {
            if (!_coalescer.ShouldEmit(build.ProjectId, build.FailureTarget))
            {
                _logger.LogDebug("Coalesced failure notification for project={ProjectId} target={Target}",
                    build.ProjectId, build.FailureTarget);
                continue;
            }

            var matches = await db.PushSubscriptions
                .AsNoTracking()
                .Where(s => s.ProjectId == build.ProjectId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var payload = new
            {
                title = $"{build.ProjectName} build failed",
                body = build.FailureTarget is { Length: > 0 } t
                    ? $"target {t} failed (exit {build.ExitCode})"
                    : $"exit {build.ExitCode}",
                url = $"/builds/{build.Id}",
                buildId = build.Id,
                projectName = build.ProjectName,
                projectArea = build.ProjectArea,
                seq = build.Seq,
            };

            foreach (var sub in matches)
                await sender.SendAsync(sub, payload, ct).ConfigureAwait(false);
        }

        _lastSeenSeq = Math.Max(_lastSeenSeq, maxSeqThisScan);
    }
}
