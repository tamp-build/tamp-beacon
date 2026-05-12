using System;
using System.IO;
using System.Text.Json;
using WebPush;

namespace Tamp.Beacon.Push;

/// <summary>
/// Loads or generates the VAPID keypair used for Web Push notifications.
/// Keys live on disk under <see cref="BeaconOptions.VapidKeyPath"/> so they
/// survive container restarts; back them up alongside the SQLite file.
/// </summary>
public sealed class VapidKeyStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public VapidDetails Details { get; }

    public string PublicKey => Details.PublicKey;

    public VapidKeyStore(string keyPath, string subject)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new ArgumentException("VAPID key path is empty", nameof(keyPath));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("VAPID subject is empty", nameof(subject));

        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(keyPath))
        {
            var json = File.ReadAllText(keyPath);
            var persisted = JsonSerializer.Deserialize<PersistedVapid>(json, JsonOpts)
                ?? throw new InvalidOperationException($"VAPID key file {keyPath} is empty or unreadable.");
            Details = new VapidDetails(subject, persisted.PublicKey, persisted.PrivateKey);
        }
        else
        {
            var generated = VapidHelper.GenerateVapidKeys();
            var persisted = new PersistedVapid(generated.PublicKey, generated.PrivateKey, subject, DateTimeOffset.UtcNow);
            File.WriteAllText(keyPath, JsonSerializer.Serialize(persisted, JsonOpts));
            Details = new VapidDetails(subject, generated.PublicKey, generated.PrivateKey);
        }
    }

    /// <summary>JSON shape persisted to <see cref="BeaconOptions.VapidKeyPath"/>.</summary>
    /// <param name="PublicKey">Base64-url-encoded P-256 public key.</param>
    /// <param name="PrivateKey">Base64-url-encoded P-256 private key.</param>
    /// <param name="Subject">VAPID subject (mailto: or https: URL identifying the sender).</param>
    /// <param name="GeneratedAt">When the keypair was generated. Operators rotate by deleting the file.</param>
    public sealed record PersistedVapid(string PublicKey, string PrivateKey, string Subject, DateTimeOffset GeneratedAt);
}
