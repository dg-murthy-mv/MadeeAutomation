using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MadeeAutomation.Admin.Web.Services;

/// <summary>An account created with a temporary password can only reach Change password until it picks its own.</summary>
public class MustChangePasswordFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true || user.FindFirst(AdminAuthService.MustChangeClaim) is null) return;

        var controller = context.RouteData.Values["controller"]?.ToString();
        if (controller is "Account" or "Error") return;

        context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
