using System.Globalization;
using System.Text.RegularExpressions;

namespace MadeeAutomation.Domain.Regional;

/// <summary>A menu entry the order logic works with - from the structured menu or parsed from old free text.</summary>
public record MenuLine(string Name, decimal? Price, IReadOnlyList<string>? Aliases = null, bool IsAvailable = true, string? Category = null,
    Guid? Id = null, int? DailyLimit = null);

/// <summary>
/// Turns the client's menu into MenuLines: the structured menu (MenuItems) when there is one,
/// otherwise the older free-text menu.
///
/// Reads the client's free-text menu ("Croissant - 45 THB", "Chicken biryani: ₹250", "Latte | 120")
/// into items with prices, so OUR code - not the AI - works out order totals.
/// One item per line. A line without a recognisable price is still an item, just unpriced.
/// </summary>
public static class MenuCatalog
{
    private static readonly Regex Line = new(
        @"^\s*[-*•]?\s*(?<name>.+?)\s*(?:[-–—:|=]|\.{2,})\s*(?:THB|INR|PHP|AED|Rs\.?|฿|₹|₱)?\s*(?<price>\d[\d,]*(?:\.\d{1,2})?)\s*(?:THB|INR|PHP|AED|baht|฿|₹|₱|/-)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static List<MenuLine> Parse(string? menuText)
    {
        var items = new List<MenuLine>();
        if (string.IsNullOrWhiteSpace(menuText)) return items;

        foreach (var raw in menuText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var m = Line.Match(line);
            if (m.Success && decimal.TryParse(m.Groups["price"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                items.Add(new MenuLine(m.Groups["name"].Value.Trim(), price));
            else
                items.Add(new MenuLine(line.TrimStart('-', '*', '•', ' '), null));
        }

        return items;
    }

    /// <summary>Structured menu when the client has one, otherwise the old free text.</summary>
    public static List<MenuLine> Build(IReadOnlyCollection<Entities.MenuItem>? items, string? legacyMenuText) =>
        items is { Count: > 0 }
            ? items.OrderBy(i => i.Category ?? "").ThenBy(i => i.SortOrder).ThenBy(i => i.Name)
                .Select(i => new MenuLine(i.Name, i.Price, i.AliasList.ToList(), i.IsAvailable, i.Category, i.Id, i.DailyLimit)).ToList()
            : Parse(legacyMenuText);

    /// <summary>
    /// Exact name or alias match first (case-insensitive), then a unique "contains" match either way.
    /// </summary>
    public static MenuLine? Match(IReadOnlyList<MenuLine> menu, string? orderedName)
    {
        if (string.IsNullOrWhiteSpace(orderedName) || menu.Count == 0) return null;
        var name = orderedName.Trim();

        IEnumerable<string> Names(MenuLine l) => new[] { l.Name }.Concat(l.Aliases ?? Array.Empty<string>());

        var exact = menu.FirstOrDefault(l => Names(l).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return exact;

        var partial = menu.Where(l => Names(l).Any(n =>
            n.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }
}
