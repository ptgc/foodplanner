using System.Text.Json;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Email;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Support;

/// <summary>
/// What a support action tells the audit row about itself (T-096): ids and counts, never recipe content, tokens, PINs, hashes
/// or passwords. Scoped per request. The handlers of the endpoints fill it; the audit filter and the writer read it.
/// </summary>
public sealed class SupportAuditScope
{
    private readonly SortedDictionary<string, object?> _detail = [];
    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];

    public Guid? SessionId { get; set; }

    public Guid? TargetHouseholdId { get; set; }

    public string? TargetHouseholdName { get; set; }

    public SupportSessionMode? Mode { get; set; }

    public SupportReasonCategory? ReasonCategory { get; set; }

    public string? ReasonNote { get; set; }

    /// <summary>The handler already saved its own audit row (the session start does, in the transaction that creates the session).</summary>
    public bool Written { get; set; }

    /// <summary>The household's Owners are emailed after the commit (a write session was started).</summary>
    public bool NotifyOwners { get; set; }

    public IReadOnlyDictionary<string, object?> DetailValues => _detail;

    public IReadOnlyList<Func<CancellationToken, Task>> AfterCommitActions => _afterCommit;

    /// <summary>Adds one small fact (an id, a count) to the row's detail JSON.</summary>
    public void Detail(string key, object? value) => _detail[key] = value;

    /// <summary>Runs once the transaction has committed (a live-sync signal). A failure is logged and never undoes the action.</summary>
    public void AfterCommit(Func<CancellationToken, Task> action) => _afterCommit.Add(action);

    public void UseSession(SupportSessionInfo session)
    {
        SessionId = session.Id;
        TargetHouseholdId = session.HouseholdId;
        Mode = session.Mode;
        ReasonCategory = session.ReasonCategory;
        ReasonNote = session.ReasonNote;
    }
}

/// <summary>
/// Builds and saves the audit rows (T-096), and mirrors each one to the structured log (category <c>SupportAudit</c>, no note
/// text). It is the only code that inserts <see cref="SupportAuditEntry"/>. A row stands alone: it copies the admin's address,
/// the household name and the session's reason.
/// </summary>
public sealed class SupportAuditWriter(
    AppDbContext db,
    ICurrentUser user,
    ISupportContext support,
    SupportAuditScope scope,
    TimeProvider time,
    ILoggerFactory loggers)
{
    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    private readonly ILogger _log = loggers.CreateLogger("SupportAudit");

    /// <summary>Adds the row to the context (the caller saves). Returns it so the caller can mirror it after the save.</summary>
    public async Task<SupportAuditEntry> StageAsync(string action, SupportAuditOutcome outcome, CancellationToken cancellationToken, bool lookUpName = true)
    {
        var session = support.Session ?? support.ActiveSession;
        var householdId = scope.TargetHouseholdId ?? support.Session?.HouseholdId;
        var name = scope.TargetHouseholdName;
        if (lookUpName && name is null && householdId is { } id)
        {
            // Households is the tenant root: not filtered, and not a bypass. Never for a refused attempt: that reads nothing of the household asked for.
            name = await db.Households.AsNoTracking().Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken);
        }

        var entry = new SupportAuditEntry(
            time.GetUtcNow(),
            user.UserId ?? Guid.Empty,
            user.Email ?? "unknown",
            scope.SessionId ?? session?.Id,
            householdId,
            name,
            action,
            scope.Mode ?? session?.Mode,
            scope.ReasonCategory ?? session?.ReasonCategory,
            scope.ReasonNote ?? session?.ReasonNote,
            scope.DetailValues.Count == 0 ? null : JsonSerializer.Serialize(scope.DetailValues, DetailJson),
            outcome);
        db.SupportAuditEntries.Add(entry);
        return entry;
    }

    /// <summary>An admin was refused: one <c>access.denied</c> row with the household that was asked for. A non-admin never gets one.</summary>
    public async Task WriteDeniedAsync(string reason, string? attemptedAction, CancellationToken cancellationToken)
    {
        scope.TargetHouseholdId = support.AttemptedHouseholdId;
        scope.Detail("reason", reason);
        scope.Detail("attemptedAction", attemptedAction);
        var entry = await StageAsync(SupportActions.AccessDenied, SupportAuditOutcome.Denied, cancellationToken, lookUpName: false);
        await db.SaveChangesAsync(cancellationToken);
        Mirror(entry);
    }

    /// <summary>The second copy: a structured log event with the facts and the reason CATEGORY, never the note.</summary>
    public void Mirror(SupportAuditEntry entry) =>
        _log.LogInformation(
            "Support audit {Action} {Outcome} admin={AdminUserId} household={TargetHouseholdId} session={SessionId} mode={Mode} category={ReasonCategory}",
            entry.Action, entry.Outcome, entry.AdminUserId, entry.TargetHouseholdId, entry.SessionId, entry.Mode, entry.ReasonCategory);
}

/// <summary>Emails every Owner of the household that support changed something (T-096). A failing email is logged and never blocks or undoes the action.</summary>
internal sealed class SupportOwnerNotifier(
    AppDbContext db, RequestCurrentHousehold household, IEmailSender email, TimeProvider time, ILogger<SupportOwnerNotifier> log)
{
    public async Task NotifyAsync(Guid householdId, Guid? sessionId, SupportReasonCategory? category, string action, CancellationToken cancellationToken)
    {
        try
        {
            // The same filtered read a session request makes: the household of an active session, no membership.
            if (household.HouseholdId != householdId)
            {
                household.SetSupport(householdId, sessionId ?? Guid.Empty);
            }

            var owners = await (from m in db.Memberships.AsNoTracking()
                                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                                where m.Role == HouseholdRole.Owner && u.Email != null
                                select u.Email!)
                .ToListAsync(cancellationToken);
            var body = $"Foodplanner support accessed your household on {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC for {category?.ToString() ?? "support"}: {action}.";
            foreach (var address in owners)
            {
                try
                {
                    await email.SendAsync(address, "Foodplanner support accessed your household", body, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogWarning(exception, "Could not email an Owner about support access to household {HouseholdId}.", householdId);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LogWarning(exception, "Could not look up the Owners to tell about support access to household {HouseholdId}.", householdId);
        }
    }
}

/// <summary>
/// The shared endpoint filter of every support endpoint (T-096). It opens a transaction, runs the action, and writes ONE audit row
/// in that same transaction before the response is sent. Fail closed: if the audit row cannot be written the request answers a
/// 500 problem, and neither the data nor the change is returned or saved. A read endpoint may save nothing but the audit row. A
/// refused or failed action rolls back and leaves a <c>Failed</c> row. After the commit, the household's Owners are emailed for
/// a write, and the handler's after-commit actions run.
/// </summary>
internal sealed class SupportAuditFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var meta = http.GetEndpoint()?.Metadata.GetMetadata<SupportAuditedMetadata>()
            ?? throw new InvalidOperationException("A support endpoint must declare SupportAuditedMetadata.");
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var scope = services.GetRequiredService<SupportAuditScope>();
        var writer = services.GetRequiredService<SupportAuditWriter>();
        var support = services.GetRequiredService<ISupportContext>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("SupportAudit");
        var cancellationToken = http.RequestAborted;

        var transaction = meta.Transactional ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        object? result;
        try
        {
            result = await next(context);
        }
        catch (Exception)
        {
            await RollBackAsync(db, transaction);
            await TryRecordAsync(writer, db, meta.Action, SupportAuditOutcome.Failed, log);
            throw;
        }

        var success = result is not IStatusCodeHttpResult { StatusCode: >= 400 };
        try
        {
            if (success && meta.Kind == SupportActionKind.View)
            {
                RequireOnlyAuditWrites(db);
            }

            if (!success)
            {
                await RollBackAsync(db, transaction);
                transaction = null;
            }

            if (!scope.Written)
            {
                var entry = await writer.StageAsync(meta.Action, success ? SupportAuditOutcome.Success : SupportAuditOutcome.Failed, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                writer.Mirror(entry);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LogError(exception, "The support audit row for {Action} could not be written; the request is refused.", meta.Action);
            await RollBackAsync(db, transaction);
            return AppProblems.Problem(
                StatusCodes.Status500InternalServerError,
                AppProblems.SupportAuditFailed,
                "Support audit failed",
                "The action was not applied because it could not be recorded.");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        if (success)
        {
            await AfterCommitAsync(services, scope, support, meta, log, cancellationToken);
        }

        return result;
    }

    private static async Task AfterCommitAsync(
        IServiceProvider services, SupportAuditScope scope, ISupportContext support, SupportAuditedMetadata meta, ILogger log, CancellationToken cancellationToken)
    {
        foreach (var action in scope.AfterCommitActions)
        {
            try
            {
                await action(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log.LogWarning(exception, "A support after-commit action failed for {Action}; the change stands.", meta.Action);
            }
        }

        var householdId = scope.TargetHouseholdId ?? support.Session?.HouseholdId;
        if ((meta.Kind == SupportActionKind.Write || scope.NotifyOwners) && householdId is { } id)
        {
            var notifier = services.GetRequiredService<SupportOwnerNotifier>();
            await notifier.NotifyAsync(id, scope.SessionId ?? support.Session?.Id, scope.ReasonCategory ?? support.Session?.ReasonCategory, meta.Action, cancellationToken);
        }
    }

    /// <summary>A read changes nothing: the only tracked change may be the audit row itself.</summary>
    private static void RequireOnlyAuditWrites(AppDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var other = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && e.Entity is not SupportAuditEntry)
            .Select(e => e.Metadata.ClrType.Name)
            .ToList();
        if (other.Count > 0)
        {
            throw new InvalidOperationException($"A support read must not change data ({string.Join(", ", other)}).");
        }
    }

    private static async Task RollBackAsync(AppDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        db.ChangeTracker.Clear();
    }

    private static async Task TryRecordAsync(SupportAuditWriter writer, AppDbContext db, string action, SupportAuditOutcome outcome, ILogger log)
    {
        try
        {
            var entry = await writer.StageAsync(action, outcome, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            writer.Mirror(entry);
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            log.LogError(exception, "The support audit row for a failed {Action} could not be written.", action);
        }
    }
}
