using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Clients");
}
