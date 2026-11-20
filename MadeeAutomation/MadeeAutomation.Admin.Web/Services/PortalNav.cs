using System.Security.Claims;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Services;

/// <summary>
/// Which menu a client's portal user sees: Orders + Menu (food & retail) or Bookings + Services + Staff
/// (beauty & wellness; "Doctors" for clinics). Read from the client on each page, so a change of business type shows at once.
/// </summary>
public class PortalNav
{
    private readonly IClientRepository _clients;
    private ClientConfig? _client;
    private bool _loaded;

    public PortalNav(IClientRepository clients) => _clients = clients;

    private async Task<ClientConfig?> ClientAsync(ClaimsPrincipal user)
    {
        if (_loaded) return _client;
        var id = ClientAccess.ClientId(user);
        _client = id is { } g ? await _clients.GetWithWorkflowsAsync(g) : null;
        _loaded = true;
        return _client;
    }

    private async Task<VerticalPack?> VerticalAsync(ClaimsPrincipal user) => (await ClientAsync(user))?.Vertical;

    /// <summary>The portal user's plan, for the banner at the top of every page (null for MadeeVision staff).</summary>
    public async Task<SubscriptionStatus?> PlanAsync(ClaimsPrincipal user) =>
        await ClientAsync(user) is { } client ? SubscriptionStatus.Of(client, DateTime.UtcNow) : null;

    /// <summary>Appointments (spa, salon, clinic) rather than orders and a menu.</summary>
    public async Task<bool> IsBeautyAsync(ClaimsPrincipal user) => (await VerticalAsync(user))?.UsesBookings() == true;

    public async Task<bool> IsClinicAsync(ClaimsPrincipal user) => await VerticalAsync(user) == VerticalPack.Clinic;
}
