using System.Text;
using System.Text.RegularExpressions;

namespace MadeeAutomation.E2E.Infrastructure;

public record TestEmail(string To, string Subject, string Body, DateTime WrittenUtc);

/// <summary>
/// The apps under test write emails as .eml files into TestResults/mail (Email:PickupDirectory)
/// instead of sending them. This reads them back: password-reset links, staff alerts.
/// </summary>
public static class Mailbox
{
    public static string Folder => Path.Combine(AppHost.LogFolder, "mail");

    public static void Clear()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        Directory.CreateDirectory(Folder);
    }

    public static List<TestEmail> To(string address)
    {
        if (!Directory.Exists(Folder)) return new();
        return Directory.GetFiles(Folder, "*.eml")
            .Select(f => Parse(File.ReadAllText(f), File.GetLastWriteTimeUtc(f)))
            .Where(m => m.To.Contains(address, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.WrittenUtc)
            .ToList();
    }

    public static Task<List<TestEmail>> WaitForAsync(string address, int count = 1, int timeoutMs = 15000) =>
        Eventually.GetAsync(() => To(address), m => m.Count >= count, timeoutMs, $"{count} email(s) to {address}");

    private static TestEmail Parse(string raw, DateTime written)
    {
        var split = Regex.Split(raw, "\r?\n\r?\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        var headerText = Regex.Replace(split[0], "\r?\n[ \t]+", " ");   // unfold
        var body = string.Join("\n\n", split.Skip(1));

        string Header(string name) =>
            Regex.Match(headerText, $"^{name}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        var encoding = Header("Content-Transfer-Encoding").ToLowerInvariant();
        if (encoding == "base64")
            body = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, "\\s", "")));
        else if (encoding == "quoted-printable")
            body = DecodeQuotedPrintable(body);

        return new TestEmail(DecodeWords(Header("To")), DecodeWords(Header("Subject")), body, written);
    }

    private static string DecodeWords(string value) =>
        Regex.Replace(value, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        {
            var bytes = m.Groups[2].Value.ToUpperInvariant() == "B"
                ? Convert.FromBase64String(m.Groups[3].Value)
                : Encoding.UTF8.GetBytes(DecodeQuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
            return Encoding.UTF8.GetString(bytes);
        });

    private static string DecodeQuotedPrintable(string input)
    {
        input = Regex.Replace(input, "=\r?\n", "");
        var bytes = new List<byte>();
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '=' && i + 2 < input.Length && Uri.IsHexDigit(input[i + 1]) && Uri.IsHexDigit(input[i + 2]))
            {
                bytes.Add(Convert.ToByte(input.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(input[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
