using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MadeeAutomation.Admin.Web.Security;

/// <summary>
/// Who is signed in: MadeeAutomation staff (no client claim - can open everything) or a client's
/// own portal user (client claim = their ClientConfigId - their own business only).
/// </summary>
public static class ClientAccess
{
    public const string ClientClaim = "madee_client";

    /// <summary>The client a portal user belongs to. Null for MadeeAutomation staff.</summary>
    public static Guid? ClientId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClientClaim), out var id) ? id : null;

    public static bool IsClientUser(ClaimsPrincipal user) => ClientId(user) is not null;
}

/// <summary>
/// Marks a controller (or one action) a client's portal user is allowed to open.
/// Everything WITHOUT this attribute is refused for portal users - so a new admin page is
/// closed to clients by default, not open by mistake.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ClientPortalAttribute : Attribute { }

/// <summary>Global rule: a client's portal user gets "Not allowed" (403) on any page not marked [ClientPortal].</summary>
public class ClientUserAccessFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!ClientAccess.IsClientUser(context.HttpContext.User)) return;

        var allowed = context.ActionDescriptor.EndpointMetadata.OfType<ClientPortalAttribute>().Any();
        if (!allowed) context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }
}

/// <summary>
/// Base for pages used by BOTH staff and a client's portal user (menu, hours, orders).
/// Staff choose the client in the URL. A portal user always gets their own client - whatever
/// client id the URL or form carries is ignored, and records of another client answer "not found".
/// </summary>
[ClientPortal]
public abstract class ClientScopedController : Controller
{
    protected Guid? OwnClientId => ClientAccess.ClientId(User);
    protected bool IsClientUser => OwnClientId is not null;

    /// <summary>The client this request works on.</summary>
    protected Guid Scope(Guid requestedClientConfigId) => OwnClientId ?? requestedClientConfigId;

    /// <summary>May the signed-in user touch a record that belongs to this client?</summary>
    protected bool CanAccess(Guid clientConfigId) => OwnClientId is null || OwnClientId == clientConfigId;

    protected string SignedInAs => User.Identity?.Name ?? "unknown";

    /// <summary>
    /// After a button on a list (sold out, accept, delete...): back to the same page of the same filtered
    /// list. returnUrl is only followed when it is a page of this site.
    /// </summary>
    protected IActionResult BackTo(string? returnUrl, IActionResult otherwise) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : otherwise;
}
