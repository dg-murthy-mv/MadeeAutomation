using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.Finance;

/// <summary>
/// Finance / Chit vertical pack.
/// Trigger: FileUploaded (a photo or PDF of a receipt/invoice, e.g. sent via WhatsApp or dropped
/// into a watched folder/blob container). Handler extracts structured data via Claude
/// and writes it to the database for reconciliation/bookkeeping.
/// HandlerKey: "invoice-extraction"
///
/// Note: the actual file bytes should be uploaded separately (see MadeeAutomation.AI for
/// how to pass base64 image/PDF content to Claude) - this handler assumes the trigger payload
/// already contains a pre-extracted text/base64 reference for simplicity.
/// </summary>
public class InvoiceExtractionHandler : IHandler
{
    public string HandlerKey => "invoice-extraction";

    private readonly IClaudeService _claude;

    public InvoiceExtractionHandler(IClaudeService claude)
    {
        _claude = claude;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(context.PayloadJson);
        var root = payload.RootElement;
        var documentText = root.TryGetProperty("extractedText", out var t) ? t.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(documentText))
        {
            return HandlerResult.Fail("No document text available to extract from.");
        }

        const string systemPrompt =
            "You extract structured data from invoices and receipts. Reply with strict JSON only: " +
            "{ \"vendor\": string, \"date\": string, \"totalAmount\": number, \"currency\": string, " +
            "\"category\": string, \"lineItems\": [ { \"description\": string, \"amount\": number } ] }";

        var extractedJson = await _claude.CompleteJsonAsync(systemPrompt, documentText, cancellationToken);

        var writeAction = ActionRequest.For(ActionType.WriteToDatabase, new Dictionary<string, string>
        {
            ["table"] = "ExtractedInvoices",
            ["dataJson"] = extractedJson,
            ["clientConfigId"] = context.ClientConfigId.ToString()
        });

        return HandlerResult.Ok("Invoice data extracted.", writeAction);
    }
}
