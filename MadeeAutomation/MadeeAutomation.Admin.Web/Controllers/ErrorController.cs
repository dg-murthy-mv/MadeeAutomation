using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

public class ErrorViewModel
{
    public int StatusCode { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RequestId { get; set; }
}

/// <summary>Friendly pages for 404 / 403 / 500 instead of browser or framework error screens.</summary>
[AllowAnonymous]
[MadeeAutomation.Admin.Web.Security.ClientPortal]
[ApiExplorerSettings(IgnoreApi = true)]
public class ErrorController : Controller
{
    private readonly ILogger<ErrorController> _logger;

    public ErrorController(ILogger<ErrorController> logger) => _logger = logger;

    [Route("Error/{statusCode:int?}")]
    public IActionResult Index(int? statusCode = null)
    {
        var code = statusCode ?? 500;
        var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        if (code >= 500)
        {
            var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            if (feature?.Error is { } ex)
                _logger.LogError(ex, "Unhandled error on {Path} (request {RequestId})", feature.Path, requestId);
        }

        var model = code switch
        {
            404 => new ErrorViewModel { Title = "Page not found", Message = "That page doesn't exist, or the record was removed." },
            403 => new ErrorViewModel { Title = "Not allowed", Message = "Your account can't open this page." },
            400 => new ErrorViewModel { Title = "Something was wrong with that request", Message = "Go back and try again." },
            _ => new ErrorViewModel { Title = "Something went wrong", Message = "The error has been logged. Try again, and if it keeps happening send the request id below." }
        };
        model.StatusCode = code;
        model.RequestId = requestId;

        Response.StatusCode = code;
        return View("~/Views/Error/Index.cshtml", model);
    }
}
