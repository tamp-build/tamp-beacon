namespace Tamp.Beacon.Push;

/// <summary>
/// Bound from the <c>Beacon</c> config section in <c>appsettings*.json</c>.
/// </summary>
public sealed class BeaconOptions
{
    public string DbPath { get; set; } = "tamp-beacon.sqlite";
    public string VapidKeyPath { get; set; } = "vapid.key";
    public string VapidSubject { get; set; } = "mailto:beacon@tamp.local";

    /// <summary>Coalescing window for failure alerts. Within this window, repeat failures
    /// on the same (project, target) tuple emit only one notification.</summary>
    public int FailureAlertWindowSeconds { get; set; } = 300;

    /// <summary>How often the failure-alert worker scans the DB for new failure builds.</summary>
    public int FailureWorkerIntervalMs { get; set; } = 1000;
}
