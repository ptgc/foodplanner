using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Api.Tenancy;

/// <summary>
/// The per-request household context (Q-004). Kept deliberately light: the cached EF model holds the first
/// context's instance. Only <see cref="HouseholdContextMiddleware"/> sets it. Both values are <c>null</c> for a
/// request outside a household route and for a caller who is not a member of the routed household. Role
/// resolution lives only here, so T-026 can narrow it to Child when a kid profile is active.
/// </summary>
internal sealed class RequestCurrentHousehold : ICurrentHousehold
{
    public Guid? HouseholdId { get; private set; }

    public HouseholdRole? Role { get; private set; }

    public void Set(Guid householdId, HouseholdRole role)
    {
        HouseholdId = householdId;
        Role = role;
    }
}
