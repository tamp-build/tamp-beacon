using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Beacon.Sdk;

/// <summary>
/// Strongly-typed client over the tamp-beacon HTTP/JSON API.
/// </summary>
/// <remarks>
/// <para>
/// Construct with an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/>
/// points at the beacon root (e.g. <c>http://localhost:4318</c>). The SDK does not own the
/// HttpClient lifecycle — consumers either pass a singleton or use
/// <see cref="System.Net.Http.IHttpClientFactory"/>.
/// </para>
/// <para>
/// All endpoints are JSON; no auth headers are required in v0.1.0 (token auth lands in 0.2.0).
/// </para>
/// </remarks>
public sealed class BeaconClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public BeaconClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<BuildList> GetBuildsAsync(
        string? project = null,
        string? area = null,
        long? sinceSeq = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildBuildsUrl(project, area, sinceSeq, limit);
        var result = await _http.GetFromJsonAsync<BuildList>(url, JsonOpts, ct).ConfigureAwait(false);
        return result ?? new BuildList();
    }

    public async Task<BuildDetail?> GetBuildAsync(long id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/builds/{id}", ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BuildDetail>(JsonOpts, ct).ConfigureAwait(false);
    }

    public async Task<ProjectList> GetProjectsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<ProjectList>("/api/projects", JsonOpts, ct).ConfigureAwait(false);
        return result ?? new ProjectList();
    }

    public async Task<TargetStatList> GetSlowestTargetsAsync(
        string? project = null,
        long? sinceUnixNs = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildStatsUrl("/api/targets/slowest", project, sinceUnixNs, limit);
        var result = await _http.GetFromJsonAsync<TargetStatList>(url, JsonOpts, ct).ConfigureAwait(false);
        return result ?? new TargetStatList();
    }

    public async Task<FlakyTargetList> GetFlakiestTargetsAsync(
        string? project = null,
        long? sinceUnixNs = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildStatsUrl("/api/targets/flakiest", project, sinceUnixNs, limit);
        var result = await _http.GetFromJsonAsync<FlakyTargetList>(url, JsonOpts, ct).ConfigureAwait(false);
        return result ?? new FlakyTargetList();
    }

    public async Task<HealthStatus> GetHealthAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<HealthStatus>("/healthz", JsonOpts, ct).ConfigureAwait(false);
        return result ?? new HealthStatus();
    }

    public async Task SubscribePushAsync(PushSubscriptionRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/push/subscribe", request, JsonOpts, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private static string BuildBuildsUrl(string? project, string? area, long? sinceSeq, int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(project)) query.Add($"project={Uri.EscapeDataString(project)}");
        if (!string.IsNullOrEmpty(area)) query.Add($"area={Uri.EscapeDataString(area)}");
        if (sinceSeq is { } s) query.Add($"since_seq={s}");
        if (limit is { } l) query.Add($"limit={l}");
        var qs = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return "/api/builds" + qs;
    }

    private static string BuildStatsUrl(string path, string? project, long? sinceUnixNs, int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(project)) query.Add($"project={Uri.EscapeDataString(project)}");
        if (sinceUnixNs is { } s) query.Add($"since_unix_ns={s}");
        if (limit is { } l) query.Add($"limit={l}");
        var qs = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return path + qs;
    }
}
