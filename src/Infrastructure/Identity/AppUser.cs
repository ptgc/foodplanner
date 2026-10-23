using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Infrastructure.Identity;

/// <summary>
/// A login identity (ADR 0004). Global, not an <c>ITenantEntity</c>: it has no HouseholdId and the tenant query
/// filter does not apply. Household membership and roles are separate (T-011).
/// </summary>
public class AppUser : IdentityUser<Guid>
{
}
