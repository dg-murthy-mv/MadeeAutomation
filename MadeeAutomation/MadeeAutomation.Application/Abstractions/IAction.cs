using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// An action is a single side-effect: send a WhatsApp message, call an external API,
/// generate a PDF, call Claude, write to the database, or notify MadeeAutomation staff.
/// Handlers return a list of ActionRequests; the ActionExecutor resolves and runs each one.
/// </summary>
public interface IAction
{
    ActionType Type { get; }

    Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Instruction to run one action, produced by a handler.</summary>
public class ActionRequest
{
    public ActionType Type { get; set; }

    /// <summary>Arbitrary parameters the action needs, e.g. { "to": "+66...", "message": "..." }</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    public static ActionRequest For(ActionType type, Dictionary<string, string> parameters) => new()
    {
        Type = type,
        Parameters = parameters
    };
}

public class ActionResult
{
    public bool Success { get; set; }
    public string? ResultData { get; set; }
    public string? ErrorMessage { get; set; }
}
