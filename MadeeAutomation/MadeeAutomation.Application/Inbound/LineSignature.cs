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
