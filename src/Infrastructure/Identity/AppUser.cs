using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Infrastructure.Identity;

/// <summary>
/// A login identity (ADR 0004). Global, not an <c>ITenantEntity</c>: it has no HouseholdId and the tenant query
/// filter does not apply. Household membership and roles are separate (T-011).
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    /// <summary>
    /// When the address was last sent a set-password link (T-074, Q-016): set on register and refreshed by every
    /// re-register outside the cooldown. Only meaningful while <see cref="IdentityUser{TKey}.EmailConfirmed"/> is false:
    /// it is cleared on confirmation, so confirmed and seeded users have null. The unconfirmed-account cleanup uses it.
    /// </summary>
    public DateTimeOffset? RegisteredAt { get; set; }
}
