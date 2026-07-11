using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.Support;

/// <summary>
/// Customer Support vertical pack.
/// Trigger: MessageReceived (WhatsApp/LINE inbound customer question).
/// Handler answers from the client's FAQ/knowledge base context if confident,
/// otherwise escalates to a human via NotifyInternal instead of guessing.
/// HandlerKey: "faq-responder"
/// </summary>
public class FaqResponderHandler : IHandler
{
    public string HandlerKey => "faq-responder";

    private readonly IClaudeService _claude;

    public FaqResponderHandler(IClaudeService claude)
    {
        _claude = claude;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(context.PayloadJson);
        var root = payload.RootElement;
        var question = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var fromAddress = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
        // In a real deployment, load this per-client from ClientConfig.IntegrationSettingsJson
        // or a dedicated FAQ table, rather than hardcoding it here.
        var faqContext = root.TryGetProperty("faqContext", out var fc) ? fc.GetString() ?? "" : "";

        const string systemPrompt =
            "You are a customer support assistant. Answer ONLY using the provided FAQ context. " +
            "Reply with strict JSON only: { \"canAnswer\": boolean, \"answer\": string|null }. " +
            "If the question is not covered by the FAQ context, set canAnswer to false.";

        var userMessage = $"FAQ context:\n{faqContext}\n\nCustomer question:\n{question}";
        var responseJson = await _claude.CompleteJsonAsync(systemPrompt, userMessage, cancellationToken);

        using var parsed = JsonDocument.Parse(responseJson);
        var canAnswer = parsed.RootElement.TryGetProperty("canAnswer", out var ca) && ca.GetBoolean();

        if (canAnswer && parsed.RootElement.TryGetProperty("answer", out var answerEl))
        {
            var replyAction = ActionRequest.For(ActionType.SendMessage, new Dictionary<string, string>
            {
                ["channel"] = "whatsapp",
                ["to"] = fromAddress,
                ["message"] = answerEl.GetString() ?? ""
            });
            return HandlerResult.Ok("Answered from FAQ.", replyAction);
        }

        var escalateAction = ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = $"Escalation needed - customer {fromAddress} asked: \"{question}\" (not covered by FAQ)"
        });

        return HandlerResult.Ok("Escalated to human - question not covered by FAQ.", escalateAction);
    }
}
