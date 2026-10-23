using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Api.Tenancy;

/// <summary>
/// Placeholder until T-011 resolves the household from the caller's membership: there is never a household
/// context, so tenant queries return nothing and tenant writes are rejected.
/// </summary>
internal sealed class PlaceholderCurrentHousehold : ICurrentHousehold
{
    public Guid? HouseholdId => null;
}
