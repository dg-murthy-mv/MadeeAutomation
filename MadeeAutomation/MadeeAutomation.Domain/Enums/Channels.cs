namespace MadeeAutomation.Domain.Enums;

/// <summary>
/// Messaging channel names. Stored as lower-case strings in WorkflowDefinition.Channel,
/// InboundEvent.Channel and ExecutionLog.Channel so the database stays readable.
/// A workflow with Channel = null listens to every channel.
/// </summary>
public static class Channels
{
    public const string Line = "line";
    public const string WhatsApp = "whatsapp";
    public const string Messenger = "messenger";

    public static readonly IReadOnlyList<string> All = new[] { Line, WhatsApp, Messenger };

    public static string? Normalize(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var c = channel.Trim().ToLowerInvariant();
        return All.Contains(c) ? c : null;
    }

    public static string DisplayName(string? channel) => channel switch
    {
        Line => "LINE",
        WhatsApp => "WhatsApp",
        Messenger => "Messenger",
        _ => "Any channel"
    };
}
