using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence.Tenancy;

namespace Foodplanner.Api.Tenancy;

/// <summary>Endpoint metadata on the group <c>/api/households/{householdId}</c>: its endpoints run in that household.</summary>
public sealed class HouseholdScopedMetadata
{
    public static HouseholdScopedMetadata Instance { get; } = new();

    private HouseholdScopedMetadata()
    {
    }
}

public static class HouseholdRoutes
{
    public const string RouteParameter = "householdId";

    /// <summary>
    /// The one group every household-scoped endpoint hangs off: <c>/api/households/{householdId}</c> (Q-004). The
    /// household context middleware resolves the caller's membership for it. Never take a household id from a body or
    /// query string. Attach policies with <c>RequireAuthorization(Policies.X)</c> on a sub-group.
    /// </summary>
    public static RouteGroupBuilder MapHouseholdScopedGroup(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup($"/households/{{{RouteParameter}:guid}}");
        group.WithMetadata(HouseholdScopedMetadata.Instance);
        return group;
    }
}

/// <summary>
/// Resolves the caller's membership once per request and sets <see cref="RequestCurrentHousehold"/>. It is a
/// middleware, not an endpoint filter, because authorization policies run before endpoint filters and the policy
/// handler needs the role. It must run after authentication and routing and before authorization.
/// </summary>
internal sealed class HouseholdContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, ICurrentUser user, RequestCurrentHousehold household, ITenantResolution resolution)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<HouseholdScopedMetadata>() is not null
            && user.UserId is { } userId
            && Guid.TryParse(context.GetRouteValue(HouseholdRoutes.RouteParameter)?.ToString(), out var householdId)
            && await resolution.FindRole(userId, householdId, context.RequestAborted) is { } role)
        {
            household.Set(householdId, role);
        }

        await next(context);
    }
}
