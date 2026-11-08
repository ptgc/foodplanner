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

    /// <summary>
    /// Whether the user row still exists. A device of a deleted account may keep a valid cookie until the next security stamp
    /// check (T-095); routes that write the user id without a membership check ask first and answer 401, never an FK error.
    /// </summary>
    Task<bool> UserExists(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// First statement of an account deletion: a no-op update of the user's own row, which holds its row lock until the end
    /// of the transaction, so a second delete of the same account queues. False when the user no longer exists.
    /// </summary>
    Task<bool> LockUser(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Every household of the user with its member and other-Owner counts, for <see cref="AccountDeletionPlan"/> (T-095).
    /// Counts come from all memberships of those households, not just the caller's. Never anyone else's households.
    /// </summary>
    Task<IReadOnlyList<AccountHouseholdFacts>> GetHouseholdFactsForUser(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every reference to the user from household data that LIVES ON, across all households (T-095, Q-027): open
    /// invites the user created are revoked; user links on invites, picks, week locks, pantry confirmations, checked items and
    /// finished imports are set to null (the rows stay as history); the user's import drafts are deleted. Bulk statements,
    /// each with an explicit user-id predicate. The caller runs it inside a transaction, after the households that go with
    /// the account are deleted, and before the user row is deleted (the user foreign keys are NO ACTION).
    /// </summary>
    Task<AccountErasureResult> EraseUserReferences(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>How many rows <see cref="ITenantResolution.EraseUserReferences"/> touched, per kind.</summary>
public sealed record AccountErasureResult(
    int InvitesRevoked,
    int InviteLinksCleared,
    int ImportDraftsDeleted,
    int ImportLinksCleared,
    int WeekLocksCleared,
    int PicksCleared,
    int PantryConfirmationsCleared,
    int CheckedItemsCleared);
