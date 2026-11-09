using System.Text.Json;

namespace MadeeAutomation.Application.Common;

/// <summary>Safe helpers for reading the JSON objects Claude returns.</summary>
public static class JsonReply
{
    /// <summary>Parses text into a JSON object. Returns null if it is not a JSON object.</summary>
    public static JsonElement? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        // Tolerate a sentence before/after the object: take the outermost {...}.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        trimmed = trimmed.Substring(start, end - start + 1);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(this JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool GetBool(this JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static int? GetInt(this JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i)) return i;
        return null;
    }
}
