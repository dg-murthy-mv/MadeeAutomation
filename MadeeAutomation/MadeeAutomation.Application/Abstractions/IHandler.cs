namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// A handler contains the business logic for one automation: it receives a TriggerContext,
/// does whatever work is needed (call Claude, call a connector, transform data), and returns
/// zero or more actions to execute. This mirrors the CQRS handler pattern already used in
/// Merkai CRM (auto-registered via Scrutor) so the pattern should feel immediately familiar.
///
/// Naming convention: HandlerKey should be a stable, kebab-case string stored on
/// WorkflowDefinition.HandlerKey, e.g. "whatsapp-order-parser", "lead-scoring", "invoice-ocr".
/// </summary>
public interface IHandler
{
    /// <summary>Stable key used to resolve this handler by WorkflowDefinition.HandlerKey.</summary>
    string HandlerKey { get; }

    Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default);
}

public class HandlerResult
{
    public bool Success { get; set; }
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ActionRequest> Actions { get; set; } = new();

    public static HandlerResult Ok(string summary, params ActionRequest[] actions) => new()
    {
        Success = true,
        Summary = summary,
        Actions = actions.ToList()
    };

    public static HandlerResult Fail(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
