using System.Security.Claims;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Services;

/// <summary>
/// Which menu a client's portal user sees: Orders + Menu (food & retail) or Bookings + Services + Staff
/// (beauty & wellness; "Doctors" for clinics). Read from the client on each page, so a change of business type shows at once.
/// </summary>
public class PortalNav
{
    private readonly IClientRepository _clients;
    private VerticalPack? _vertical;

    public PortalNav(IClientRepository clients) => _clients = clients;

    private async Task<VerticalPack?> VerticalAsync(ClaimsPrincipal user)
    {
        if (_vertical is { } known) return known;
        var id = ClientAccess.ClientId(user);
        var client = id is { } g ? await _clients.GetWithWorkflowsAsync(g) : null;
        _vertical = client?.Vertical;
        return _vertical;
    }

    /// <summary>Appointments (spa, salon, clinic) rather than orders and a menu.</summary>
    public async Task<bool> IsBeautyAsync(ClaimsPrincipal user) => (await VerticalAsync(user))?.UsesBookings() == true;

    public async Task<bool> IsClinicAsync(ClaimsPrincipal user) => await VerticalAsync(user) == VerticalPack.Clinic;
}
