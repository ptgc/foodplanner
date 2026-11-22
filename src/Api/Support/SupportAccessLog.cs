using Foodplanner.Domain.Support;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Support;

/// <summary>One audit row as the household sees it: no admin, no note, no detail (T-096).</summary>
public sealed record SupportAccessItem(
    DateTimeOffset At, SupportSessionMode? Mode, SupportReasonCategory? ReasonCategory, string Action, SupportAuditOutcome Outcome);

/// <summary>One audit row as the admin sees it in the support access log: the household's view plus the address and the note.</summary>
public sealed record SupportAdminAccessItem(
    DateTimeOffset At,
    SupportSessionMode? Mode,
    SupportReasonCategory? ReasonCategory,
    string Action,
    SupportAuditOutcome Outcome,
    string AdminEmail,
    string? ReasonNote);

public sealed record SupportAccessPage(SupportAccessItem[] Items, int Page, int PageSize, int Total);

public sealed record SupportAdminAccessPage(SupportAdminAccessItem[] Items, int Page, int PageSize, int Total);

/// <summary>
/// Reads the support audit log for ONE household (T-096). Only rows that touched the household, never <c>access.denied</c>
/// rows (no data of that household was accessed) or search rows (they carry no household), for the last 90 days. The household id
/// is always the policy-verified one (a household member's context, or the session's), never a client value. Keeps the audit table
/// out of feature code.
/// </summary>
public sealed class SupportAccessLog(AppDbContext db, TimeProvider time)
{
    public const int WindowDays = 90;
    public const int MaxPageSize = 50;
    public const int DefaultPageSize = 20;

    private IQueryable<SupportAuditEntry> Rows(Guid householdId)
    {
        var since = time.GetUtcNow().AddDays(-WindowDays);
        return db.SupportAuditEntries.AsNoTracking()
            .Where(e => e.TargetHouseholdId == householdId && e.Outcome != SupportAuditOutcome.Denied && e.At >= since);
    }

    public async Task<SupportAccessPage> ForHouseholdAsync(Guid householdId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = Rows(householdId);
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new SupportAccessItem(e.At, e.Mode, e.ReasonCategory, e.Action, e.Outcome))
            .ToArrayAsync(cancellationToken);
        return new SupportAccessPage(items, page, pageSize, total);
    }

    public async Task<SupportAdminAccessPage> ForAdminAsync(Guid householdId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = Rows(householdId);
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new SupportAdminAccessItem(e.At, e.Mode, e.ReasonCategory, e.Action, e.Outcome, e.AdminEmail, e.ReasonNote))
            .ToArrayAsync(cancellationToken);
        return new SupportAdminAccessPage(items, page, pageSize, total);
    }
}
