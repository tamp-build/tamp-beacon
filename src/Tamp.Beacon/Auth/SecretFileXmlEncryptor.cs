using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Symmetric AES-256-GCM encryption-at-rest for the data-protection key
/// ring (TAM-219). The 32-byte AES key is read from disk at construction;
/// each XML element gets a fresh 12-byte nonce, encrypted with a 16-byte
/// auth tag. Wire format inside the on-disk &lt;key&gt; element:
///
///   &lt;encryptedKey kind="tamp-beacon-aes-gcm-v1"&gt;
///     &lt;value&gt;{base64(nonce || tag || ciphertext)}&lt;/value&gt;
///   &lt;/encryptedKey&gt;
///
/// The "kind" attribute lets a future version migrate to a different
/// AEAD without breaking decryption of old ring entries.
/// </summary>
internal sealed class SecretFileXmlEncryptor : IXmlEncryptor
{
    private const string ElementName = "encryptedKey";
    private const string KindAttribute = "kind";
    private const string KindV1 = "tamp-beacon-aes-gcm-v1";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly byte[] _key;

    public SecretFileXmlEncryptor(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _key = ReadKey(keyPath);
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(_key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var wire = new byte[NonceBytes + TagBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, wire, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, wire, NonceBytes, TagBytes);
        Buffer.BlockCopy(ciphertext, 0, wire, NonceBytes + TagBytes, ciphertext.Length);

        var element = new XElement(ElementName,
            new XAttribute(KindAttribute, KindV1),
            new XElement("value", Convert.ToBase64String(wire)));

        return new EncryptedXmlInfo(element, typeof(SecretFileXmlDecryptor));
    }

    internal static byte[] ReadKey(string keyPath)
    {
        var bytes = File.ReadAllBytes(keyPath);
        if (bytes.Length != KeyBytes)
        {
            throw new InvalidOperationException(
                $"SecretFile at '{keyPath}' must be exactly {KeyBytes} bytes (got {bytes.Length}). " +
                $"Generate with e.g. `head -c {KeyBytes} /dev/urandom > {keyPath}` or `openssl rand {KeyBytes} > {keyPath}`.");
        }
        return bytes;
    }
}

/// <summary>
/// Decrypter half of <see cref="SecretFileXmlEncryptor"/>. Resolved by
/// the data-protection ring via DI based on the type embedded in
/// <see cref="EncryptedXmlInfo.DecryptorType"/>.
/// </summary>
internal sealed class SecretFileXmlDecryptor : IXmlDecryptor
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public SecretFileXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The decryptor is instantiated by the data-protection framework's
        // ActivatorUtilities, so we resolve our options from DI rather than
        // taking them as constructor args.
        var opts = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
        if (string.IsNullOrWhiteSpace(opts.KeyProtection.SecretFilePath))
        {
            throw new InvalidOperationException(
                "SecretFileXmlDecryptor was resolved but Beacon:Auth:KeyProtection:SecretFilePath is unset. " +
                "This usually means the key ring was previously encrypted under SecretFile mode but the operator " +
                "switched Mode away from SecretFile without rotating the keys first. Restore the previous mode + key " +
                "to decrypt the ring, then rotate.");
        }
        _key = SecretFileXmlEncryptor.ReadKey(opts.KeyProtection.SecretFilePath);
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var valueEl = encryptedElement.Element("value")
            ?? throw new CryptographicException("encrypted key-ring element is missing the inner <value> node");

        var wire = Convert.FromBase64String(valueEl.Value);
        if (wire.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("encrypted key-ring payload is shorter than the AES-GCM nonce + tag");
        }

        var nonce = new byte[NonceBytes];
        var tag = new byte[TagBytes];
        var ciphertext = new byte[wire.Length - NonceBytes - TagBytes];
        Buffer.BlockCopy(wire, 0, nonce, 0, NonceBytes);
        Buffer.BlockCopy(wire, NonceBytes, tag, 0, TagBytes);
        Buffer.BlockCopy(wire, NonceBytes + TagBytes, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_key, TagBytes))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
    }
}
