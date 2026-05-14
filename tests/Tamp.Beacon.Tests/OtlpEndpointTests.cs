using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// OTLP ingest contract — bearer auth, content-type negotiation, scope
/// rejection, last_used_at refresh, ProjectId FK persistence. Each test
/// stands up its own project + token via the slice 2/3 endpoints, then
/// exercises the slice 4 receive path.
/// </summary>
public sealed class OtlpEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";

    public OtlpEndpointTests(BeaconAppFixture fx) => _fx = fx;

    private async Task<(string slug, string token, long tokenId)> CreateProjectAndTokenAsync(System.Net.Http.HttpClient admin, string namePrefix)
    {
        var slug = $"{namePrefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(15, namePrefix.Length + 9));
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = slug });
        var mint = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "smoke" });
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>();
        return (slug, body.GetProperty("token").GetString()!, body.GetProperty("id").GetInt64());
    }

    private static async Task<HttpResponseMessage> PostProtoAsync(
        System.Net.Http.HttpClient client, string token, byte[] body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        System.Net.Http.HttpClient client, string token, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Rejects_Missing_Authorization_With_401()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        var resp = await _fx.Client.PostAsync("/v1/traces", new ByteArrayContent(Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_Bogus_Token_With_401()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var anyClient = _fx.Factory.CreateClient();
        anyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tbk_not-real-at-all");
        var resp = await anyClient.PostAsync("/v1/traces",
            new StringContent("{\"resourceSpans\":[]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_Revoked_Token_With_401()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token, tokenId) = await CreateProjectAndTokenAsync(admin, "rvk");

        var revoke = await admin.DeleteAsync($"/api/projects/{slug}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var resp = await PostJsonAsync(_fx.Factory.CreateClient(), token, "{\"resourceSpans\":[]}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_NonTamp_Scope_With_422()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (_, token, _) = await CreateProjectAndTokenAsync(admin, "non");

        var req = OtlpFixtures.NonTampScope();
        var resp = await PostProtoAsync(_fx.Factory.CreateClient(), token, OtlpFixtures.ToProto(req));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_Unknown_ContentType_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (_, token, _) = await CreateProjectAndTokenAsync(admin, "ct");

        using var client = _fx.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new StringContent("garbage", System.Text.Encoding.UTF8, "text/plain"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Protobuf_Happy_Path_Persists_Build_With_ProjectId()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token, tokenId) = await CreateProjectAndTokenAsync(admin, "pb");

        var req = OtlpFixtures.TrivialBuildSuccess(project: slug);
        var resp = await PostProtoAsync(_fx.Factory.CreateClient(), token, OtlpFixtures.ToProto(req));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();

        // Find the project so we can match by id.
        var project = await db.Projects.SingleAsync(p => p.Slug == slug);
        var build = await db.Builds.SingleAsync(b => b.ProjectId == project.Id);
        Assert.Equal(slug, build.ProjectName);
        Assert.Equal("success", build.Outcome);
        Assert.Equal(5, build.TargetsTotal);
        Assert.Equal("github", build.CiVendor);
        Assert.True(build.Seq > 0);

        // Token last_used_at advanced.
        var t = await db.ProjectTokens.SingleAsync(x => x.Id == tokenId);
        Assert.NotNull(t.LastUsedAt);

        // The build_summary span event landed.
        var ev = await db.Events.SingleAsync(e => e.BuildId == build.Id);
        Assert.Equal("tamp.build.summary", ev.Name);
    }

    [Fact]
    public async Task Json_Happy_Path_Persists_Build_With_ProjectId()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token, _) = await CreateProjectAndTokenAsync(admin, "js");

        var req = OtlpFixtures.TrivialBuildSuccess(project: slug);
        var resp = await PostJsonAsync(_fx.Factory.CreateClient(), token, OtlpFixtures.ToJson(req));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Slug == slug);
        var build = await db.Builds.SingleAsync(b => b.ProjectId == project.Id);
        Assert.Equal("success", build.Outcome);
    }

    [Fact]
    public async Task Metrics_Endpoint_Accepts_Empty_Body_With_Bearer()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (_, token, _) = await CreateProjectAndTokenAsync(admin, "m");

        using var client = _fx.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/metrics")
        {
            Content = new StringContent("{\"resourceMetrics\":[]}", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Build_Seq_Is_Monotonic_Across_Two_Ingests()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var (slug, token, _) = await CreateProjectAndTokenAsync(admin, "sq");

        var first = OtlpFixtures.TrivialBuildSuccess(
            project: slug,
            traceIdHex: Guid.NewGuid().ToString("N"),
            spanIdHex: Guid.NewGuid().ToString("N").Substring(0, 16));
        var second = OtlpFixtures.TrivialBuildSuccess(
            project: slug,
            traceIdHex: Guid.NewGuid().ToString("N"),
            spanIdHex: Guid.NewGuid().ToString("N").Substring(0, 16));

        await PostProtoAsync(_fx.Factory.CreateClient(), token, OtlpFixtures.ToProto(first));
        await PostProtoAsync(_fx.Factory.CreateClient(), token, OtlpFixtures.ToProto(second));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Slug == slug);
        var seqs = await db.Builds.Where(b => b.ProjectId == project.Id).OrderBy(b => b.Seq).Select(b => b.Seq).ToListAsync();
        Assert.Equal(2, seqs.Count);
        Assert.True(seqs[1] > seqs[0], $"expected monotonic seq, got {seqs[0]} then {seqs[1]}");
    }
}
