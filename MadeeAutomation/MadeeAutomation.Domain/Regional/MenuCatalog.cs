using System.Globalization;
using System.Text.RegularExpressions;

namespace MadeeAutomation.Domain.Regional;

public record MenuItem(string Name, decimal? Price);

/// <summary>
/// Reads the client's free-text menu ("Croissant - 45 THB", "Chicken biryani: ₹250", "Latte | 120")
/// into items with prices, so OUR code - not the AI - works out order totals.
/// One item per line. A line without a recognisable price is still an item, just unpriced.
/// </summary>
public static class MenuCatalog
{
    private static readonly Regex Line = new(
        @"^\s*[-*•]?\s*(?<name>.+?)\s*(?:[-–—:|=]|\.{2,})\s*(?:THB|INR|PHP|AED|Rs\.?|฿|₹|₱)?\s*(?<price>\d[\d,]*(?:\.\d{1,2})?)\s*(?:THB|INR|PHP|AED|baht|฿|₹|₱|/-)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static List<MenuItem> Parse(string? menuText)
    {
        var items = new List<MenuItem>();
        if (string.IsNullOrWhiteSpace(menuText)) return items;

        foreach (var raw in menuText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var m = Line.Match(line);
            if (m.Success && decimal.TryParse(m.Groups["price"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                items.Add(new MenuItem(m.Groups["name"].Value.Trim(), price));
            else
                items.Add(new MenuItem(line.TrimStart('-', '*', '•', ' '), null));
        }

        return items;
    }

    /// <summary>Exact name match first (case-insensitive), then a unique "contains" match either way.</summary>
    public static MenuItem? Match(IReadOnlyList<MenuItem> menu, string? orderedName)
    {
        if (string.IsNullOrWhiteSpace(orderedName) || menu.Count == 0) return null;
        var name = orderedName.Trim();

        var exact = menu.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var partial = menu.Where(i =>
            i.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(i.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }
}
