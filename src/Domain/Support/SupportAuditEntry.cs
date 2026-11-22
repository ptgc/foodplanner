namespace Foodplanner.Domain.Support;

/// <summary>
/// One line of the support audit log (T-096): who, what, which household, when, why. Append-only: the save interceptor
/// refuses to update or delete it and a database trigger does the same for every login. A global record, not household
/// data (not an ITenantEntity); the household is a plain column named <see cref="TargetHouseholdId"/>, with no foreign key,
/// and the names are snapshots, so the row stays readable after the household or the account is gone. It never holds
/// recipe content, tokens, PINs, hashes or passwords.
/// </summary>
public sealed class SupportAuditEntry
{
    public const int MaxDetailLength = 1000;

    private SupportAuditEntry()
    {
    }

    public SupportAuditEntry(
        DateTimeOffset at,
        Guid adminUserId,
        string adminEmail,
        Guid? sessionId,
        Guid? targetHouseholdId,
        string? targetHouseholdName,
        string action,
        SupportSessionMode? mode,
        SupportReasonCategory? reasonCategory,
        string? reasonNote,
        string? detail,
        SupportAuditOutcome outcome)
    {
        Id = Guid.NewGuid();
        At = at;
        AdminUserId = adminUserId;
        AdminEmail = adminEmail;
        SessionId = sessionId;
        TargetHouseholdId = targetHouseholdId;
        TargetHouseholdName = targetHouseholdName;
        Action = action;
        Mode = mode;
        ReasonCategory = reasonCategory;
        ReasonNote = reasonNote;
        Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
        Outcome = outcome;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset At { get; private set; }

    public Guid AdminUserId { get; private set; }

    public string AdminEmail { get; private set; } = string.Empty;

    public Guid? SessionId { get; private set; }

    public Guid? TargetHouseholdId { get; private set; }

    public string? TargetHouseholdName { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public SupportSessionMode? Mode { get; private set; }

    public SupportReasonCategory? ReasonCategory { get; private set; }

    public string? ReasonNote { get; private set; }

    public string? Detail { get; private set; }

    public SupportAuditOutcome Outcome { get; private set; }
}
