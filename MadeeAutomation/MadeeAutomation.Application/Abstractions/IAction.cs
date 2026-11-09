using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// An action is a single side-effect: send a message, call an external API,
/// write to the database, or notify MadeeAutomation staff.
/// Handlers return a list of ActionRequests; the WorkflowEngine resolves and runs each one.
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

    /// <summary>Arbitrary parameters the action needs, e.g. { "to": "U123...", "message": "..." }</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// When a required action fails, the whole run is marked Failed (so a reply that never reached
    /// the customer shows up red in the logs). Optional actions - like internal staff notifications -
    /// only record their failure.
    /// </summary>
    public bool Required { get; set; } = true;

    public static ActionRequest For(ActionType type, Dictionary<string, string> parameters, bool required = true) => new()
    {
        Type = type,
        Parameters = parameters,
        Required = required
    };
}

public class ActionResult
{
    public bool Success { get; set; }
    public string? ResultData { get; set; }
    public string? ErrorMessage { get; set; }

    public static ActionResult Ok(string? data = null) => new() { Success = true, ResultData = data };
    public static ActionResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}
