using System.Security.Cryptography;
using System.Text;
using AML.Application.Common;
using Microsoft.Extensions.Configuration;

namespace AML.Infrastructure.Security;

/// <summary>
/// Application-level encryption (AES-256-GCM) and keyed HMAC tokens. Keys come from configuration supplied by environment
/// variables or a secret manager (never source control). Ciphertext format: v1:{keyId}:{nonce}:{cipher}:{tag} (base64).
/// </summary>
public sealed class CryptoService : ICryptoService
{
    private readonly byte[] _encKey;
    private readonly byte[] _hmacKey;
    private readonly byte[] _integrityKey;
    private readonly string _keyId;

    public CryptoService(IConfiguration config)
    {
        _encKey = Key(config, "Security:EncryptionKey", 32);
        _hmacKey = Key(config, "Security:SearchTokenKey", 32);
        _integrityKey = Key(config, "Security:IntegrityKey", 32);
        _keyId = config["Security:EncryptionKeyId"] ?? "k1";
    }

    private static byte[] Key(IConfiguration c, string name, int len)
    {
        var v = c[name];
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Missing secret '{name}'. Provide it through environment variables (e.g. {name.Replace(":", "__")}) or a secret manager.");
        var bytes = Convert.FromBase64String(v);
        if (bytes.Length < len) throw new InvalidOperationException($"Secret '{name}' must be at least {len} bytes (base64).");
        return bytes[..len];
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encKey, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return $"v1:{_keyId}:{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(cipher)}:{Convert.ToBase64String(tag)}";
    }

    public string Decrypt(string ciphertext)
    {
        var p = ciphertext.Split(':');
        if (p.Length != 5 || p[0] != "v1") throw new CryptographicException("Unsupported ciphertext format.");
        var nonce = Convert.FromBase64String(p[2]);
        var cipher = Convert.FromBase64String(p[3]);
        var tag = Convert.FromBase64String(p[4]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string SearchToken(string value)
    {
        var normalized = new string(value.Trim().ToUpperInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray());
        return Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(normalized)));
    }

    public string IntegrityHash(string value) => Convert.ToHexString(HMACSHA256.HashData(_integrityKey, Encoding.UTF8.GetBytes(value)));

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256") return false;
        var iterations = int.Parse(p[1]);
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
