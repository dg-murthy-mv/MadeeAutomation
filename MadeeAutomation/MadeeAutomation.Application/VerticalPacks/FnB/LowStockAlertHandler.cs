using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.FnB;

/// <summary>
/// F&amp;B / Retail vertical pack.
/// Trigger: Scheduled (e.g. daily 7am). Checks POSFirst for low-stock items and
/// notifies the client's ops staff so nothing runs out mid-day.
/// HandlerKey: "low-stock-alert"
/// </summary>
public class LowStockAlertHandler : IHandler
{
    public string HandlerKey => "low-stock-alert";

    private readonly IPosFirstConnector _posFirst;

    public LowStockAlertHandler(IPosFirstConnector posFirst)
    {
        _posFirst = posFirst;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var lowStockItems = await _posFirst.GetLowStockItemsAsync(context.ClientConfigId, cancellationToken);

        if (lowStockItems.Count == 0)
        {
            return HandlerResult.Ok("No low-stock items today.");
        }

        var message = "Low stock alert:\n" + string.Join("\n", lowStockItems.Select(i => $"- {i}"));

        var notifyAction = ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = message
        });

        return HandlerResult.Ok($"{lowStockItems.Count} low-stock item(s) flagged.", notifyAction);
    }
}
