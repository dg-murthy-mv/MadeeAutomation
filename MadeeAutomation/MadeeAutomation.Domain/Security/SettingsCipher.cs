using System.Security.Cryptography;
using System.Text;

namespace MadeeAutomation.Domain.Security;

/// <summary>
/// Encrypts / decrypts ClientConfig.IntegrationSettingsJson with AES-256-GCM.
///
/// Stored format:  "enc:v1:" + base64( nonce[12] | tag[16] | ciphertext )
///
/// Pure code with no dependencies, so the WebApi, Admin.Web and the E2E test project all use
/// exactly the same implementation. The 32-byte key comes from configuration
/// (Security:SettingsEncryptionKey, base64) - the SAME key must be set in WebApi and Admin.Web.
/// </summary>
public static class SettingsCipher
{
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static bool IsEncrypted(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Parses a base64 key and checks it is exactly 32 bytes (AES-256).</summary>
    public static byte[] ParseKey(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException(
                "Security:SettingsEncryptionKey is not configured. Generate one (see SETUP.md) and set it with " +
                "dotnet user-secrets in BOTH MadeeAutomation.WebApi and MadeeAutomation.Admin.Web.");

        byte[] key;
        try { key = Convert.FromBase64String(base64Key.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("Security:SettingsEncryptionKey is not valid base64."); }

        if (key.Length != 32)
            throw new InvalidOperationException($"Security:SettingsEncryptionKey must be 32 bytes (got {key.Length}).");

        return key;
    }

    public static string Encrypt(string plainText, byte[] key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);

        return Prefix + Convert.ToBase64String(packed);
    }

    public static string Decrypt(string stored, byte[] key)
    {
        if (!IsEncrypted(stored)) return stored; // legacy plain JSON

        var packed = Convert.FromBase64String(stored.Substring(Prefix.Length));
        if (packed.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted settings value is too short.");

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipher = packed.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
