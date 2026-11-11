using System.Security.Cryptography;
using System.Text;

namespace MadeeAutomation.Application.Inbound;

/// <summary>
/// LINE signs every webhook: X-Line-Signature = base64( HMAC-SHA256( channelSecret, raw body bytes ) ).
/// Checking it proves the call really came from LINE for THIS client's channel - without it,
/// anyone who learns the URL could create fake orders.
/// </summary>
public static class LineSignature
{
    public static string Compute(string channelSecret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        return Convert.ToBase64String(hmac.ComputeHash(body));
    }

    public static bool IsValid(string? channelSecret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(channelSecret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;

        byte[] expected, actual;
        try
        {
            expected = Convert.FromBase64String(Compute(channelSecret.Trim(), body));
            actual = Convert.FromBase64String(signatureHeader.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// <summary>
/// Meta (WhatsApp / Messenger) signs every webhook: X-Hub-Signature-256 = "sha256=" + hex( HMAC-SHA256( app secret, raw body ) ).
/// The App Secret is in the Meta developer console: App settings -> Basic -> App secret.
/// </summary>
public static class MetaSignature
{
    public static string Compute(string appSecret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    public static bool IsValid(string? appSecret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var header = signatureHeader.Trim();
        if (!header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;

        byte[] actual;
        try
        {
            actual = Convert.FromHexString(header[7..]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret.Trim()));
        return CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(body), actual);
    }
}
