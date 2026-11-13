using System.Security.Claims;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Services;

/// <summary>
/// Which menu a client's portal user sees: Orders + Menu (food & retail) or Bookings + Services + Staff
/// (beauty & wellness). Read from the client on each page, so a change of business type shows at once.
/// </summary>
public class PortalNav
{
    private readonly IClientRepository _clients;
    private bool? _beauty;

    public PortalNav(IClientRepository clients) => _clients = clients;

    public async Task<bool> IsBeautyAsync(ClaimsPrincipal user)
    {
        if (_beauty is { } known) return known;
        var id = ClientAccess.ClientId(user);
        var client = id is { } g ? await _clients.GetWithWorkflowsAsync(g) : null;
        _beauty = client?.Vertical == VerticalPack.BeautyWellness;
        return _beauty.Value;
    }
}
