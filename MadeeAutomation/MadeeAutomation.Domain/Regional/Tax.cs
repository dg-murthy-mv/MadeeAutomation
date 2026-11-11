using System.Globalization;

namespace MadeeAutomation.Domain.Regional;

/// <summary>The money lines of one order: what the items add up to, the tax in it (or on top), and what the customer pays.</summary>
public record TaxBreakdown(decimal Subtotal, decimal TaxAmount, decimal Total, string? TaxName, decimal? RatePercent, bool PricesIncludeTax)
{
    public bool HasTax => TaxName is not null && RatePercent is > 0;

    /// <summary>"VAT 7%" / "GST 2.5%".</summary>
    public string Label => HasTax ? Tax.Label(TaxName!, RatePercent!.Value) : string.Empty;
}

/// <summary>
/// One tax per client (GST, VAT...), worked out by our code - never by the AI.
///   - Menu prices INCLUDE the tax (usual for cafes): the total stays the sum of the prices and we
///     show how much of it is tax.
///   - Menu prices do NOT include it: the tax is added on top.
/// A client with no tax name or rate (not tax-registered) gets no tax lines at all.
/// Amounts are rounded to 2 decimals, halves away from zero.
/// </summary>
public static class Tax
{
    public static bool IsConfigured(string? taxName, decimal? ratePercent) =>
        !string.IsNullOrWhiteSpace(taxName) && ratePercent is > 0;

    public static string Label(string taxName, decimal ratePercent) =>
        $"{taxName.Trim()} {ratePercent.ToString("0.##", CultureInfo.InvariantCulture)}%";

    public static TaxBreakdown Calculate(decimal itemsTotal, string? taxName, decimal? ratePercent, bool pricesIncludeTax)
    {
        if (!IsConfigured(taxName, ratePercent))
            return new TaxBreakdown(itemsTotal, 0m, itemsTotal, null, null, pricesIncludeTax);

        var rate = ratePercent!.Value / 100m;
        if (pricesIncludeTax)
        {
            var tax = Math.Round(itemsTotal - itemsTotal / (1m + rate), 2, MidpointRounding.AwayFromZero);
            return new TaxBreakdown(itemsTotal - tax, tax, itemsTotal, taxName!.Trim(), ratePercent, true);
        }

        var added = Math.Round(itemsTotal * rate, 2, MidpointRounding.AwayFromZero);
        return new TaxBreakdown(itemsTotal, added, itemsTotal + added, taxName!.Trim(), ratePercent, false);
    }

    /// <summary>
    /// The total lines shown to the customer and to staff:
    ///   no tax        "Total: ฿150.00"
    ///   included      "Total: ฿150.00 (includes VAT 7%: ฿9.81)"
    ///   added on top  "Subtotal: ฿150.00" / "VAT 7%: ฿10.50" / "Total: ฿160.50"
    /// </summary>
    public static List<string> Lines(TaxBreakdown b, string? currency)
    {
        if (!b.HasTax) return new() { $"Total: {Money.Format(b.Total, currency)}" };
        if (b.PricesIncludeTax)
            return new() { $"Total: {Money.Format(b.Total, currency)} (includes {b.Label}: {Money.Format(b.TaxAmount, currency)})" };
        return new()
        {
            $"Subtotal: {Money.Format(b.Subtotal, currency)}",
            $"{b.Label}: {Money.Format(b.TaxAmount, currency)}",
            $"Total: {Money.Format(b.Total, currency)}"
        };
    }
}
