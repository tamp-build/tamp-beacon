using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// End-to-end TAM-219 check — bring the whole app up with
/// Mode=SecretFile, sign in, and round-trip the session cookie through
/// /me. If our custom <c>SecretFileXmlEncryptor</c> is not actually
/// wired into the data-protection ring, the cookie's encrypted payload
/// won't decrypt on the next request and /me returns 401 (or the test
/// runs an in-memory ring, where re-issuing the cookie produces output
/// the framework can't parse on its own next read).
/// </summary>
public sealed class KeyProtectionSecretFileFixture : BeaconAppFixture
{
    public string SecretKeyPath { get; }

    public KeyProtectionSecretFileFixture()
    {
        SecretKeyPath = Path.Combine(Path.GetTempPath(), $"beacon-test-kp-{Guid.NewGuid():N}.key");
        File.WriteAllBytes(SecretKeyPath, RandomNumberGenerator.GetBytes(32));

        ConfigOverrides["Beacon:Auth:KeyProtection:Mode"] = "SecretFile";
        ConfigOverrides["Beacon:Auth:KeyProtection:SecretFilePath"] = SecretKeyPath;
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (File.Exists(SecretKeyPath))
        {
            try { File.Delete(SecretKeyPath); } catch { /* best-effort */ }
        }
    }
}

public sealed class KeyProtectionIntegrationTests : IClassFixture<KeyProtectionSecretFileFixture>
{
    private readonly KeyProtectionSecretFileFixture _fx;
    private const string AdminUser = "scott";
    private const string AdminPassword = "correct-horse-battery-staple";

    public KeyProtectionIntegrationTests(KeyProtectionSecretFileFixture fx) => _fx = fx;

    [Fact]
    public async Task Cookie_Session_Round_Trips_Under_SecretFile_Mode()
    {
        await _fx.EnsureAdminAsync(AdminUser, AdminPassword);

        using var client = _fx.CreateCookieClient();
        var login = await client.PostAsJsonAsync("/break-glass", new { username = AdminUser, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The cookie issued at login was encrypted by the data-protection
        // ring. The /me call below decrypts it on a separate request — if
        // the custom encrypter is wired incorrectly (e.g. the framework
        // can't find the matching decrypter type via DI), this fails 401.
        var me = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var body = await me.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(AdminUser, body.GetProperty("username").GetString());
    }

    [Fact]
    public void Key_Ring_Files_On_Disk_Do_Not_Contain_Cleartext_Key_Material()
    {
        // Force the ring to materialize a key by performing an actual
        // Protect() call — this is the same path the cookie auth handler
        // takes and reliably triggers FileSystemXmlRepository.StoreElement
        // through the configured XmlEncryptor.
        using var scope = _fx.Factory.Services.CreateScope();
        var dpp = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var protector = dpp.CreateProtector("tamp-beacon.test.kp");
        _ = protector.Protect("ensure-key-ring-flushes-to-disk");

        var dpKeyDir = ResolveDataProtectionDir();
        Assert.True(Directory.Exists(dpKeyDir),
            $"data-protection key directory '{dpKeyDir}' should exist after Protect()");

        var keyFiles = Directory.GetFiles(dpKeyDir, "key-*.xml");
        Assert.True(keyFiles.Length > 0,
            $"expected key files under '{dpKeyDir}' after Protect(); directory contents: {string.Join(", ", Directory.GetFiles(dpKeyDir))}");

        foreach (var f in keyFiles)
        {
            var xml = File.ReadAllText(f);
            // Positive proof: our encryptor's wire marker is present, with the
            // version attribute (so a future format bump is obvious).
            Assert.Contains("tamp-beacon-aes-gcm-v1", xml);
            // Negative proof: the unencrypted format wraps the raw key bytes
            // in a <masterKey>...<value>BASE64</value></masterKey> element.
            // If we see <masterKey>, the encrypter wasn't wired and adopters
            // would be shipping plaintext keys to disk.
            Assert.DoesNotContain("<masterKey", xml);
        }
    }

    private string ResolveDataProtectionDir()
    {
        // The fixture sets Beacon:Auth:DataProtectionKeyDirectory to a per-
        // fixture tmp dir; pull it back out of the running host's IOptions.
        using var scope = _fx.Factory.Services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        return opts.DataProtectionKeyDirectory;
    }
}
