using System.Security.Claims;
using Foodplanner.Api.Authorization;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Foodplanner.Api.Tenancy;

/// <summary>
/// The household context for code that has no route to resolve it from: SignalR hub methods, where the household id
/// is an argument the client chose and must never be trusted. It does what <see cref="HouseholdContextMiddleware"/>
/// does for a request (resolve the caller's membership, set <see cref="RequestCurrentHousehold"/>) and then asks the
/// same <see cref="Policies.HouseholdMember"/> policy, so no role is compared here (CLAUDE.md rule 2). Call it at the
/// start of every hub method, on the method's own scope: after it succeeds the scoped DbContext is filtered to that
/// household.
/// </summary>
internal sealed class HouseholdMembershipGuard(
    ITenantResolution resolution, RequestCurrentHousehold household, IAuthorizationService authorization)
{
    /// <summary>True when the connection's user is a member of the household; the scope is then in that household.</summary>
    public async Task<bool> TryEnterAsync(HubCallerContext connection, Guid householdId, CancellationToken cancellationToken)
    {
        var principal = connection.User;
        if (principal?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || householdId == Guid.Empty
            || await resolution.FindRole(userId, householdId, cancellationToken) is not { } role)
        {
            return false;
        }

        household.Set(householdId, role);
        return (await authorization.AuthorizeAsync(principal, Policies.HouseholdMember)).Succeeded;
    }
}
