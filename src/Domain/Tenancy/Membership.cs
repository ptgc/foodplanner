using Foodplanner.Domain.Common;

namespace Foodplanner.Domain.Tenancy;

/// <summary>A user in a household with a role. Unique on (HouseholdId, UserId).</summary>
public sealed class Membership : Entity, ITenantEntity
{
    private Membership()
    {
    }

    public Membership(Guid householdId, Guid userId, HouseholdRole role)
    {
        if (householdId == Guid.Empty)
        {
            throw new ArgumentException("A membership needs a household.", nameof(householdId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        HouseholdId = householdId;
        UserId = userId;
        Role = role;
    }

    public Guid HouseholdId { get; private set; }

    public Guid UserId { get; private set; }

    public HouseholdRole Role { get; private set; }

    /// <summary>
    /// Sets the role (T-048). Whether this may happen (for example never demoting the last Owner) depends on the other
    /// memberships, so the caller decides; this only guards the value.
    /// </summary>
    public void ChangeRole(HouseholdRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        Role = role;
    }
}
