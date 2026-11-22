namespace Foodplanner.Domain.Support;

/// <summary>
/// A support session (T-096): one system admin, ONE household, a mode and a reason, for a short time. A global record about
/// support, not household data: it is not an ITenantEntity and has no foreign keys, so deleting the household or the account
/// never deletes or alters it. At most one session per admin is active (a unique filtered index backs this up).
/// </summary>
public sealed class SupportSession
{
    public const int MinNoteLength = 10;
    public const int MaxNoteLength = 500;

    private SupportSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid AdminUserId { get; private set; }

    public Guid TargetHouseholdId { get; private set; }

    public SupportSessionMode Mode { get; private set; }

    public SupportReasonCategory ReasonCategory { get; private set; }

    public string ReasonNote { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndedReason { get; private set; }

    /// <summary>Starts a session. The lifetime is fixed here and cannot be extended: a new reason means a new session.</summary>
    public static SupportSession Start(
        Guid adminUserId,
        Guid targetHouseholdId,
        SupportSessionMode mode,
        SupportReasonCategory category,
        string reasonNote,
        DateTimeOffset now,
        TimeSpan readLifetime,
        TimeSpan writeLifetime)
    {
        if (adminUserId == Guid.Empty || targetHouseholdId == Guid.Empty)
        {
            throw new ArgumentException("A support session needs an admin and a household.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var note = NormalizeNote(reasonNote) ?? throw new ArgumentException("Invalid reason note.", nameof(reasonNote));
        return new SupportSession
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            TargetHouseholdId = targetHouseholdId,
            Mode = mode,
            ReasonCategory = category,
            ReasonNote = note,
            StartedAt = now,
            ExpiresAt = now + (mode == SupportSessionMode.Write ? writeLifetime : readLifetime),
        };
    }

    /// <summary>Trims the note; null when it is shorter than <see cref="MinNoteLength"/> or longer than <see cref="MaxNoteLength"/> after trimming.</summary>
    public static string? NormalizeNote(string? note)
    {
        var trimmed = note?.Trim();
        return trimmed is { Length: >= MinNoteLength and <= MaxNoteLength } ? trimmed : null;
    }

    public bool IsActive(DateTimeOffset now) => EndedAt is null && ExpiresAt > now;

    /// <summary>Ends the session once; ending an ended session changes nothing.</summary>
    public void End(string reason, DateTimeOffset now)
    {
        if (EndedAt is not null)
        {
            return;
        }

        EndedAt = now;
        EndedReason = reason;
    }
}
