using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Admin.Web.Services;

namespace MadeeAutomation.Admin.Web.Models;

public class WorkflowTestModel
{
    public Guid WorkflowId { get; set; }
    public Guid ClientConfigId { get; set; }
    public string? WorkflowName { get; set; }
    public string? ClientName { get; set; }
    public string? ClientTimeZoneId { get; set; }
    public string? ClientCurrency { get; set; }

    [Required(ErrorMessage = "Type what a customer might send.")]
    [StringLength(2000)]
    [Display(Name = "Customer message")]
    public string Message { get; set; } = string.Empty;

    public string? Channel { get; set; } = "line";

    public WorkflowTestResult? Result { get; set; }
}
