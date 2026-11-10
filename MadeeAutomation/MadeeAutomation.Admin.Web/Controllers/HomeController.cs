using MadeeAutomation.Admin.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>The start page: staff go to the client list, a client's portal user goes to their own portal.</summary>
[ClientPortal]
public class HomeController : Controller
{
    public IActionResult Index() => ClientAccess.IsClientUser(User)
        ? RedirectToAction("Index", "Portal")
        : RedirectToAction("Index", "Clients");
}
