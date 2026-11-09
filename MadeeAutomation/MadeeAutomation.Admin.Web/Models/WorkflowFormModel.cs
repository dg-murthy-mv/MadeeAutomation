using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Models;

public class WorkflowFormModel
{
    public Guid? Id { get; set; }
    public Guid ClientConfigId { get; set; }
    public string? ClientName { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Workflow name")]
    public string Name { get; set; } = string.Empty;

    [Required, Display(Name = "Handler")]
    public string HandlerKey { get; set; } = "customer-message";

    public VerticalPack Vertical { get; set; } = VerticalPack.FnBRetail;

    [Display(Name = "Trigger type")]
    public TriggerType TriggerType { get; set; } = TriggerType.MessageReceived;

    /// <summary>"" = any channel, otherwise line / whatsapp / messenger. Only used by message handlers.</summary>
    public string? Channel { get; set; } = "line";

    [Display(Name = "Trigger config (JSON)")]
    public string TriggerConfigJson { get; set; } = "{}";

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public bool IsEdit => Id.HasValue;
}
