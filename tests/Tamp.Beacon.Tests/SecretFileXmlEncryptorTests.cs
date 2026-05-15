using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Pure-crypto unit tests for the TAM-219 AES-256-GCM XmlEncryptor pair.
/// Covers round-trip, wrong-size key rejection, tamper detection.
/// </summary>
public sealed class SecretFileXmlEncryptorTests : IDisposable
{
    private readonly string _keyPath;

    public SecretFileXmlEncryptorTests()
    {
        _keyPath = Path.Combine(Path.GetTempPath(), $"beacon-test-key-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(_keyPath, RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        if (File.Exists(_keyPath))
        {
            try { File.Delete(_keyPath); } catch { /* best-effort */ }
        }
    }

    private IServiceProvider BuildDecryptorServices(string keyPath)
    {
        var services = new ServiceCollection();
        services.Configure<AuthOptions>(opts => opts.KeyProtection = new KeyProtectionOptions
        {
            Mode = KeyProtectionMode.SecretFile,
            SecretFilePath = keyPath,
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Round_Trip_Of_KeyRing_Element_Returns_Original_Xml()
    {
        var encryptor = new SecretFileXmlEncryptor(_keyPath);
        var plaintext = new XElement("key",
            new XAttribute("id", "abc-123"),
            new XElement("descriptor", new XCData("opaque-key-bytes-from-the-DP-ring")));

        var info = encryptor.Encrypt(plaintext);
        Assert.NotNull(info);
        Assert.Equal(typeof(SecretFileXmlDecryptor), info.DecryptorType);
        Assert.NotEqual(plaintext.ToString(), info.EncryptedElement.ToString());

        var decryptor = new SecretFileXmlDecryptor(BuildDecryptorServices(_keyPath));
        var roundTripped = decryptor.Decrypt(info.EncryptedElement);

        Assert.Equal(plaintext.ToString(SaveOptions.DisableFormatting),
                     roundTripped.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Two_Encryptions_Of_Same_Plaintext_Produce_Different_Ciphertext()
    {
        // Confirms a fresh nonce is being generated per call — same plaintext
        // under the same key must not produce identical wire bytes, otherwise
        // an observer learns when two ring entries hold the same key material.
        var encryptor = new SecretFileXmlEncryptor(_keyPath);
        var plaintext = new XElement("key", "static-content");

        var a = encryptor.Encrypt(plaintext);
        var b = encryptor.Encrypt(plaintext);

        var aValue = a.EncryptedElement.Element("value")!.Value;
        var bValue = b.EncryptedElement.Element("value")!.Value;
        Assert.NotEqual(aValue, bValue);
    }

    [Theory]
    [InlineData(16)]   // half-length
    [InlineData(31)]   // off-by-one
    [InlineData(33)]   // off-by-one the other way
    [InlineData(64)]   // someone tried "longer = stronger"
    [InlineData(0)]    // empty file
    public void Wrong_Size_Key_File_Throws_With_Actionable_Message(int bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"beacon-test-bad-key-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes == 0 ? Array.Empty<byte>() : RandomNumberGenerator.GetBytes(bytes));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new SecretFileXmlEncryptor(path));
            Assert.Contains("32 bytes", ex.Message);
            Assert.Contains(bytes.ToString(), ex.Message);     // surfaces what we actually saw
            Assert.Contains("/dev/urandom", ex.Message);       // surfaces a recipe for fixing it
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tampered_Ciphertext_Throws_AuthenticationTagMismatch()
    {
        var encryptor = new SecretFileXmlEncryptor(_keyPath);
        var info = encryptor.Encrypt(new XElement("key", "tampered-target"));

        // Flip a byte in the encrypted payload — AES-GCM's auth tag must catch this.
        var valueEl = info.EncryptedElement.Element("value")!;
        var wire = Convert.FromBase64String(valueEl.Value);
        wire[wire.Length - 1] ^= 0x01;
        valueEl.Value = Convert.ToBase64String(wire);

        var decryptor = new SecretFileXmlDecryptor(BuildDecryptorServices(_keyPath));
        Assert.Throws<AuthenticationTagMismatchException>(() => decryptor.Decrypt(info.EncryptedElement));
    }

    [Fact]
    public void Decrypter_With_Different_Key_File_Fails_Cleanly()
    {
        // Operator swapped the secret file (intentionally or by accident) —
        // the framework should fail with a crypto exception, not return
        // garbage that the data-protection layer might trust.
        var encryptor = new SecretFileXmlEncryptor(_keyPath);
        var info = encryptor.Encrypt(new XElement("key", "original"));

        var otherKeyPath = Path.Combine(Path.GetTempPath(), $"beacon-test-other-key-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(otherKeyPath, RandomNumberGenerator.GetBytes(32));
        try
        {
            var decryptor = new SecretFileXmlDecryptor(BuildDecryptorServices(otherKeyPath));
            Assert.Throws<AuthenticationTagMismatchException>(() => decryptor.Decrypt(info.EncryptedElement));
        }
        finally
        {
            File.Delete(otherKeyPath);
        }
    }

    [Fact]
    public void Decrypter_Throws_Helpful_Message_When_SecretFilePath_Unset()
    {
        // Misconfiguration: ring entries on disk look encrypted but the
        // operator's appsettings has Mode=None (e.g. they switched modes
        // without rotating). The decryptor should fail FAST with an
        // operator-actionable hint, not silently produce garbage.
        var services = new ServiceCollection();
        services.Configure<AuthOptions>(opts => opts.KeyProtection = new KeyProtectionOptions
        {
            Mode = KeyProtectionMode.None,
            SecretFilePath = null,
        });
        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => new SecretFileXmlDecryptor(sp));
        Assert.Contains("Mode away from SecretFile", ex.Message);
    }
}
