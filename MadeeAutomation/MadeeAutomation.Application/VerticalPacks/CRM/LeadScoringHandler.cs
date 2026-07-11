using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.CRM;

/// <summary>
/// CRM / Sales vertical pack.
/// Trigger: Webhook (e.g. a lead capture form submission, or a Facebook Lead Ad).
/// Handler: asks Claude to score/qualify the lead, then writes it into Merkai CRM
/// via IMerkaiConnector with the score attached as a note.
/// HandlerKey: "lead-scoring"
/// </summary>
public class LeadScoringHandler : IHandler
{
    public string HandlerKey => "lead-scoring";

    private readonly IClaudeService _claude;
    private readonly IMerkaiConnector _merkai;

    public LeadScoringHandler(IClaudeService claude, IMerkaiConnector merkai)
    {
        _claude = claude;
        _merkai = merkai;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(context.PayloadJson);
        var root = payload.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        var company = root.TryGetProperty("company", out var c) ? c.GetString() ?? "" : "";

        const string systemPrompt =
            "You score inbound B2B leads from 1 (low intent) to 5 (high intent, ready to buy) based on " +
            "the message they submitted. Reply with strict JSON only: " +
            "{ \"score\": number, \"reason\": string, \"suggestedNextAction\": string }";

        var userMessage = $"Name: {name}\nCompany: {company}\nEmail: {email}\nMessage: {message}";
        var scoringJson = await _claude.CompleteJsonAsync(systemPrompt, userMessage, cancellationToken);

        var leadPayload = new { name, email, company, source = "web-form", rawMessage = message };
        var leadId = await _merkai.CreateOrUpdateLeadAsync(context.ClientConfigId, leadPayload, cancellationToken);

        await _merkai.AddDealNoteAsync(context.ClientConfigId, leadId, $"AI lead score: {scoringJson}", cancellationToken);

        var internalNotify = ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = $"New lead scored and added to Merkai: {name} ({company}) - {scoringJson}"
        });

        return HandlerResult.Ok($"Lead {leadId} scored and synced to Merkai.", internalNotify);
    }
}
