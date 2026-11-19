namespace Foodplanner.Domain.Tenancy;

/// <summary>
/// What the account's deletion means for one household the user belongs to (T-095). <paramref name="MemberCount"/> counts
/// every membership of the household, the user's own included; <paramref name="OtherOwnerCount"/> counts the Owners other
/// than the user.
/// </summary>
public sealed record AccountHouseholdFacts(Guid HouseholdId, string Name, HouseholdRole Role, int MemberCount, int OtherOwnerCount);

/// <summary>
/// Pure rules for deleting a user account (T-095, Q-027). Every household of the user falls in exactly one group:
/// <list type="bullet">
/// <item><see cref="ToDelete"/>: the user is the only member, so the household goes with the account.</item>
/// <item><see cref="Blocking"/>: the user is the only Owner and other members exist; deletion is refused until ownership is
/// transferred (T-048) or the others have left. A household must never be left without an Owner.</item>
/// <item><see cref="Remaining"/>: the household lives on without the user (another Owner exists, or the user is no Owner).</item>
/// </list>
/// All groups are ordered by name, then id.
/// </summary>
public sealed class AccountDeletionPlan
{
    private AccountDeletionPlan(
        IReadOnlyList<AccountHouseholdFacts> toDelete, IReadOnlyList<AccountHouseholdFacts> blocking, IReadOnlyList<AccountHouseholdFacts> remaining)
    {
        ToDelete = toDelete;
        Blocking = blocking;
        Remaining = remaining;
    }

    public IReadOnlyList<AccountHouseholdFacts> ToDelete { get; }

    public IReadOnlyList<AccountHouseholdFacts> Blocking { get; }

    public IReadOnlyList<AccountHouseholdFacts> Remaining { get; }

    public bool CanDelete => Blocking.Count == 0;

    public static AccountDeletionPlan Create(IEnumerable<AccountHouseholdFacts> households)
    {
        ArgumentNullException.ThrowIfNull(households);
        var ordered = households
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.HouseholdId)
            .ToList();
        var toDelete = ordered.Where(h => h.MemberCount <= 1).ToList();
        var blocking = ordered.Where(h => h.MemberCount > 1 && h.Role == HouseholdRole.Owner && h.OtherOwnerCount == 0).ToList();
        var remaining = ordered.Except(toDelete).Except(blocking).ToList();
        return new AccountDeletionPlan(toDelete, blocking, remaining);
    }

    /// <summary>True when the households to delete are exactly the ones the person was shown and confirmed (order ignored).</summary>
    public bool ToDeleteIs(IEnumerable<Guid> confirmedHouseholdIds) =>
        ToDelete.Select(h => h.HouseholdId).Order().SequenceEqual(confirmedHouseholdIds.Distinct().Order());
}

/// <summary>The typed confirmation of an account deletion (T-095): the account's own email address.</summary>
public static class AccountDeletionConfirmation
{
    /// <summary>Case-insensitive and ignoring surrounding spaces, so a phone keyboard's capital letter does not matter.</summary>
    public static bool Matches(string? typed, string email) =>
        !string.IsNullOrWhiteSpace(typed) && string.Equals(typed.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);
}
