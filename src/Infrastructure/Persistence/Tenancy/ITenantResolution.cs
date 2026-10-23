using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>A household the user belongs to, with the user's role there.</summary>
public sealed record HouseholdMembership(Guid HouseholdId, string Name, HouseholdRole Role);

/// <summary>Outcome of <see cref="ITenantResolution.AddMembershipFromInvite"/>.</summary>
public enum InviteRedemptionOutcome
{
    /// <summary>The invite was consumed and the membership created.</summary>
    Joined,

    /// <summary>Unknown, expired or already used. The three cases are deliberately indistinguishable.</summary>
    InvalidInvite,

    /// <summary>The user already belongs to the invite's household. The invite is not consumed.</summary>
    AlreadyMember,
}

public sealed record InviteRedemptionResult(InviteRedemptionOutcome Outcome, Guid? HouseholdId = null, HouseholdRole? Role = null);

/// <summary>
/// The only code that may call <c>IgnoreQueryFilters</c> or open an <see cref="ITenantWriteScope"/> (Q-006, T-011;
/// the enforcing architecture test is T-012). Every filter bypass has an explicit user-id, household-id or token-hash
/// predicate, and every write scope names its household explicitly. It exists because some lookups happen before
/// there is a household context: finding the caller's memberships, and redeeming an invite by token.
/// </summary>
public interface ITenantResolution
{
    /// <summary>All households the user belongs to, ordered by name. Never anyone else's.</summary>
    Task<IReadOnlyList<HouseholdMembership>> GetMembershipsForUser(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's role in the household, or <c>null</c> when the user is not a member.</summary>
    Task<HouseholdRole?> FindRole(Guid userId, Guid householdId, CancellationToken cancellationToken);

    /// <summary>Looks an invite up by the SHA-256 hash of its token. For inspection; redemption goes through <see cref="AddMembershipFromInvite"/>.</summary>
    Task<Invite?> FindInviteByTokenHash(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>Creates the household and the Owner membership of <paramref name="ownerUserId"/> in one transaction.</summary>
    Task<HouseholdMembership> CreateHouseholdWithOwner(string name, Guid ownerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically consumes the invite (only if unaccepted and unexpired at <paramref name="now"/>) and creates the
    /// membership in one transaction. Two parallel calls for one token produce one <see cref="InviteRedemptionOutcome.Joined"/>.
    /// </summary>
    Task<InviteRedemptionResult> AddMembershipFromInvite(byte[] tokenHash, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}
