using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Auth;

/// <summary>
/// argon2id password hashing. Output format is the standard PHC string
/// (<c>$argon2id$v=19$m=...,t=...,p=...$salt$hash</c>) so we can rotate
/// work factors without a schema migration — the parameters travel with
/// the hash.
/// </summary>
public sealed class PasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    private readonly AuthOptions _opts;

    public PasswordHasher(IOptions<AuthOptions> opts)
    {
        _opts = opts.Value;
    }

    public string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("password must not be empty", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeArgon2(password, salt, _opts.Argon2MemoryKib, _opts.Argon2Iterations, _opts.Argon2Parallelism);

        return string.Format(
            "$argon2id$v=19$m={0},t={1},p={2}${3}${4}",
            _opts.Argon2MemoryKib,
            _opts.Argon2Iterations,
            _opts.Argon2Parallelism,
            Base64Url(salt),
            Base64Url(hash));
    }

    public bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
            return false;

        // Format: $argon2id$v=19$m=...,t=...,p=...$salt$hash
        var parts = encoded.Split('$', StringSplitOptions.None);
        if (parts.Length != 6 || parts[1] != "argon2id") return false;

        var paramSeg = parts[3];
        var memoryKib = 0;
        var iterations = 0;
        var parallelism = 0;
        foreach (var kv in paramSeg.Split(','))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) return false;
            var key = kv[..eq];
            if (!int.TryParse(kv[(eq + 1)..], out var value)) return false;
            switch (key)
            {
                case "m": memoryKib = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }
        if (memoryKib <= 0 || iterations <= 0 || parallelism <= 0) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = FromBase64Url(parts[4]);
            expected = FromBase64Url(parts[5]);
        }
        catch
        {
            return false;
        }

        var actual = ComputeArgon2(password, salt, memoryKib, iterations, parallelism);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeArgon2(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKib,
        };
        return argon2.GetBytes(HashSizeBytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
