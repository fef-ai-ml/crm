using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Endpoints;

public static class ProfilesEndpointExtensions
{
    /// <summary>
    /// Maps GET /profiles, /profiles/{id} and /profiles/{id}/events onto a group the HOST has already secured
    /// (AdminOrViewer policy, Profiles tag): the policy names live in Tenancy, and a module may not reference another module.
    /// </summary>
    public static RouteGroupBuilder MapProfilesEndpoints(this RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);
        ProfileEndpoints.Map(secured);
        return secured;
    }
}
