using Foodplanner.Domain.Support;

namespace Foodplanner.Api.Support;

/// <summary>Endpoint metadata on the whole group <c>/api/support</c>: the support middleware and the denial handler act on it.</summary>
public sealed class SupportRouteMetadata
{
    public static SupportRouteMetadata Instance { get; } = new();

    private SupportRouteMetadata()
    {
    }
}

/// <summary>Endpoint metadata on <c>/api/support/households/{targetHouseholdId}</c>: its endpoints run in the SESSION's household.</summary>
public sealed class SupportScopedMetadata
{
    public static SupportScopedMetadata Instance { get; } = new();

    private SupportScopedMetadata()
    {
    }
}

/// <summary>What a support endpoint does, which decides how it is audited and guarded.</summary>
public enum SupportActionKind
{
    /// <summary>Looks only. Saves nothing but the audit row (enforced by the audit filter).</summary>
    View,

    /// <summary>One of the four named fixes: needs a write session, and the household's Owners are emailed.</summary>
    Write,

    /// <summary>Starting, reading or ending the admin's own session.</summary>
    Session,
}

/// <summary>
/// Every support endpoint carries this: the audit action it writes and its kind. <see cref="Transactional"/> false is only for
/// the session start, which counts password failures and creates the session itself and writes its own audit row.
/// </summary>
public sealed class SupportAuditedMetadata(string action, SupportActionKind kind, bool transactional = true)
{
    public string Action { get; } = action;

    public SupportActionKind Kind { get; } = kind;

    public bool Transactional { get; } = transactional;
}

public static class SupportRoutes
{
    /// <summary>The route parameter of the support household group. Not named <c>householdId</c>: that name is reserved for /api/households/{householdId} (HouseholdRouteRulesTests).</summary>
    public const string RouteParameter = "targetHouseholdId";
}

/// <summary>An active support session as the middleware and the policies see it.</summary>
public sealed record SupportSessionInfo(
    Guid Id,
    Guid AdminUserId,
    Guid HouseholdId,
    SupportSessionMode Mode,
    SupportReasonCategory ReasonCategory,
    string ReasonNote,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt);

public enum SupportDenial
{
    None,
    SessionRequired,
    SessionMismatch,
}

/// <summary>
/// What the support middleware found for this request (T-096). Read by the policy handlers and the audit code only
/// (architecture test): feature code never touches it. <see cref="Session"/> is set only when an admin has an active
/// session for exactly the routed household.
/// </summary>
public interface ISupportContext
{
    /// <summary>The caller is a system admin (evaluated per request). False outside support routes.</summary>
    bool IsAdmin { get; }

    /// <summary>The active session whose household equals the route household, or null.</summary>
    SupportSessionInfo? Session { get; }

    /// <summary>The admin's active session even when it is for another household (for the audit row of a denial).</summary>
    SupportSessionInfo? ActiveSession { get; }

    SupportDenial Denial { get; }

    /// <summary>The household id of the route when the request was refused, for the audit row.</summary>
    Guid? AttemptedHouseholdId { get; }
}

internal sealed class RequestSupportContext : ISupportContext
{
    public bool IsAdmin { get; private set; }

    public SupportSessionInfo? Session { get; private set; }

    public SupportSessionInfo? ActiveSession { get; private set; }

    public SupportDenial Denial { get; private set; }

    public Guid? AttemptedHouseholdId { get; private set; }

    public void SetAdmin() => IsAdmin = true;

    /// <summary>The admin's active session on a route that is not household-scoped: only for the audit row, it grants nothing.</summary>
    public void NoteActive(SupportSessionInfo session) => ActiveSession = session;

    public void Matched(SupportSessionInfo session, Guid routeHouseholdId)
    {
        Session = session;
        ActiveSession = session;
        AttemptedHouseholdId = routeHouseholdId;
    }

    public void Refused(SupportDenial denial, SupportSessionInfo? active, Guid? routeHouseholdId)
    {
        Denial = denial;
        ActiveSession = active;
        AttemptedHouseholdId = routeHouseholdId;
    }
}
