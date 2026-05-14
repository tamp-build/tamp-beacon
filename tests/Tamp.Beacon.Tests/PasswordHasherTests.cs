using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Argon2id hashing contract — round-trip + boundary cases. Uses the
/// production work factor (m=64MiB, t=3) since these tests gate the
/// auth surface and a downgraded factor would let a fast-but-broken
/// implementation pass.
/// </summary>
public sealed class PasswordHasherTests
{
    private static PasswordHasher Create(AuthOptions? overrides = null)
    {
        var opts = overrides ?? new AuthOptions();
        return new PasswordHasher(Options.Create(opts));
    }

    [Fact]
    public void Hash_RoundTrip_Verifies()
    {
        var hasher = Create();
        var hash = hasher.Hash("correct-horse-battery-staple");
        Assert.True(hasher.Verify("correct-horse-battery-staple", hash));
    }

    [Fact]
    public void Hash_RejectsEmpty()
    {
        var hasher = Create();
        Assert.Throws<ArgumentException>(() => hasher.Hash(""));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hasher = Create();
        var hash = hasher.Hash("correct-horse-battery-staple");
        Assert.False(hasher.Verify("correct-horse-battery-stapleX", hash));
        Assert.False(hasher.Verify("Correct-Horse-Battery-Staple", hash));
        Assert.False(hasher.Verify("", hash));
    }

    [Fact]
    public void Verify_ProducesDifferentHashes_OnRepeatedCalls()
    {
        // Different salt each time — two hashes of the same password must
        // differ at the bytewise level even though both round-trip.
        var hasher = Create();
        var a = hasher.Hash("same-password-123");
        var b = hasher.Hash("same-password-123");
        Assert.NotEqual(a, b);
        Assert.True(hasher.Verify("same-password-123", a));
        Assert.True(hasher.Verify("same-password-123", b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("$argon2id$broken$$$")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1")]                        // missing salt + hash
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$badsalt")]                // missing hash
    [InlineData("$bcrypt$v=2a$10$salt$hash")]                              // wrong algorithm prefix
    [InlineData("$argon2id$v=19$mNAN,t=3,p=1$salt$hash")]                  // unparseable param
    [InlineData("$argon2id$v=19$m=0,t=0,p=0$salt$hash")]                   // zero params
    public void Verify_ReturnsFalse_OnMalformedHash(string? encoded)
    {
        var hasher = Create();
        Assert.False(hasher.Verify("any-password-here", encoded!));
    }

    [Theory]
    [InlineData("ascii-password-12345")]
    [InlineData("with spaces and    tabs\t")]
    [InlineData("emoji-🔐-password-123")]
    [InlineData("漢字パスワード123")]
    [InlineData("!@#$%^&*()_+-=[]{}|;':\",./<>?")]
    public void Hash_HandlesUnicodeAndSymbols(string password)
    {
        var hasher = Create();
        var hash = hasher.Hash(password);
        Assert.True(hasher.Verify(password, hash));
    }

    [Fact]
    public void Hash_HandlesVeryLongPassword()
    {
        // 4 KiB password — argon2 chews input via Blake2b so any length is fine.
        var hasher = Create();
        var pw = new string('x', 4096);
        var hash = hasher.Hash(pw);
        Assert.True(hasher.Verify(pw, hash));
        Assert.False(hasher.Verify(new string('x', 4095), hash));
    }

    [Fact]
    public void Hash_RespectsConfiguredWorkFactors()
    {
        // Bump iterations on the second hasher — produced hashes must encode
        // the new t= parameter and cross-verification must still succeed
        // because work factors travel with the hash.
        var lo = Create(new AuthOptions { Argon2Iterations = 2, Argon2MemoryKib = 16 * 1024 });
        var hi = Create(new AuthOptions { Argon2Iterations = 5, Argon2MemoryKib = 16 * 1024 });
        var hashLo = lo.Hash("portable-pw-123");
        var hashHi = hi.Hash("portable-pw-123");
        Assert.Contains("t=2", hashLo);
        Assert.Contains("t=5", hashHi);
        // Either hasher can verify either hash — parameters in the encoded
        // string drive verification, not the verifier's current config.
        Assert.True(lo.Verify("portable-pw-123", hashHi));
        Assert.True(hi.Verify("portable-pw-123", hashLo));
    }
}
