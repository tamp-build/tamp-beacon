using System;
using System.ComponentModel.DataAnnotations;

namespace Tamp.Beacon;

/// <summary>
/// Bound from the <c>Beacon</c> config section. Storage + bootstrap settings;
/// auth-specific config lives in <see cref="Auth.AuthOptions"/>.
/// </summary>
public sealed class BeaconOptions
{
    /// <summary>
    /// Postgres connection string. The single-image container deploys a bundled
    /// Postgres reached via Unix socket at <c>/var/lib/tamp-beacon/postgres</c>;
    /// adopters wanting an external Postgres set
    /// <c>BEACON_DB_CONNECTION_STRING</c> which overrides this value.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } =
        "Host=/var/lib/tamp-beacon/postgres;Username=beacon;Database=beacon";

    /// <summary>
    /// Path to the on-disk file that stores the setup-token printed at first
    /// boot. Persists across restarts until the token is consumed via
    /// <c>POST /setup</c> or expires. File lives on the PVC so a pod restart
    /// doesn't strand the operator.
    /// </summary>
    public string SetupTokenPath { get; set; } = "/var/lib/tamp-beacon/setup.token";

    /// <summary>
    /// VAPID key path for Web Push (Slice 6). Loaded lazily — the slice-1
    /// boot path does not generate or read this file.
    /// </summary>
    public string VapidKeyPath { get; set; } = "/var/lib/tamp-beacon/vapid.key";

    /// <summary>
    /// VAPID <c>sub</c> claim. Same lazy posture as <see cref="VapidKeyPath"/>.
    /// </summary>
    public string VapidSubject { get; set; } = "mailto:beacon@tamp.local";

    /// <summary>
    /// Coalesce window for failure alerts. Within this window, repeat
    /// failures on the same (project, target) tuple emit one push, not N.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, 24 * 60 * 60)]
    public int FailureAlertWindowSeconds { get; set; } = 300;

    /// <summary>
    /// FailureAlertWorker scan interval. The worker is cheap when there
    /// are no new failures (one SELECT MAX(seq)); 1s default is fine.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(50, 60_000)]
    public int FailureWorkerIntervalMs { get; set; } = 1000;
}
