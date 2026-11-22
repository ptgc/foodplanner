using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Api.Tenancy;

/// <summary>
/// The per-request household context (Q-004). Kept deliberately light: the cached EF model holds the first
/// context's instance. Only <see cref="HouseholdContextMiddleware"/> sets it (and, for an active support session, the support
/// middleware, T-096). Both values are <c>null</c> for a request outside a household route and for a caller who is not a member
/// of the routed household. Role resolution lives only here, so T-026 can narrow it to Child when a kid profile is active.
/// </summary>
internal sealed class RequestCurrentHousehold : ICurrentHousehold
{
    public Guid? HouseholdId { get; private set; }

    public HouseholdRole? Role { get; private set; }

    /// <summary>The support session this request runs under (T-096), or null. Never set for a member's request.</summary>
    public Guid? SupportSessionId { get; private set; }

    public void Set(Guid householdId, HouseholdRole role)
    {
        HouseholdId = householdId;
        Role = role;
    }

    /// <summary>
    /// A support session for exactly one household (T-096): the household is set, the role stays <c>null</c> (no membership, so
    /// no member policy passes). Called with the household of the SESSION row, never with a route value.
    /// </summary>
    public void SetSupport(Guid sessionHouseholdId, Guid sessionId)
    {
        HouseholdId = sessionHouseholdId;
        Role = null;
        SupportSessionId = sessionId;
    }
}
