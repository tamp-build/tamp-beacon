using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Probe contract for <c>/healthz</c> (liveness) and <c>/readyz</c>
/// (readiness). Liveness must answer 200 regardless of DB state; readiness
/// reports the setup-complete flag so an operator can spot a pre-bootstrap
/// pod via <c>kubectl get pods --field-selector=status.phase=Running</c>.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;

    public HealthEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Healthz_Returns_200_With_Status_Ok()
    {
        var resp = await _fx.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", doc.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_Returns_200_With_AwaitingSetup_True_On_Fresh_Database()
    {
        var resp = await _fx.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", doc.GetProperty("status").GetString());
        Assert.False(doc.GetProperty("setup_complete").GetBoolean());
        Assert.True(doc.GetProperty("awaiting_setup").GetBoolean());
    }

    [Fact]
    public async Task Healthz_Has_No_Authentication_Requirement()
    {
        // Healthz must answer without any auth header — kubelet's probe
        // doesn't carry credentials.
        var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, "/healthz");
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
