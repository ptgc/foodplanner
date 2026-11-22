using Foodplanner.Api.Features.Auth;
using Foodplanner.Api.Features.Support;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Support;

/// <summary>
/// Starting, reading and ending the admin's own support session (T-096). It lives outside <c>Features</c> on purpose: only
/// policy, middleware and this service touch the session table. The admin has at most one active session; a write session
/// needs the admin's own password again, checked with the sign-in lockout (a wrong one counts as a failed sign-in).
/// </summary>
public sealed class SupportSessionService(
    AppDbContext db,
    ICurrentUser user,
    UserManager<AppUser> users,
    PasswordProof passwords,
    LoginEmailRateLimiter emailLimiter,
    IOptionsMonitor<SupportOptions> options,
    TimeProvider time,
    SupportAuditScope scope,
    SupportAuditWriter audit)
{
    private const int MaxPasswordLength = 128;
    private const int MaxStartAttempts = 3;

    public async Task<IResult> StartAsync(StartSupportSessionRequest? request, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } adminId)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        var errors = new Dictionary<string, string[]>();
        if (request?.HouseholdId is null || request.HouseholdId == Guid.Empty)
        {
            errors["householdId"] = ["Choose a household."];
        }

        if (!StrictEnum.TryParse<SupportSessionMode>(request?.Mode, out var mode))
        {
            errors["mode"] = ["The mode must be Read or Write."];
        }

        if (!StrictEnum.TryParse<SupportReasonCategory>(request?.ReasonCategory, out var category))
        {
            errors["reasonCategory"] = [$"The reason category must be one of {string.Join(", ", Enum.GetNames<SupportReasonCategory>())}."];
        }

        var note = SupportSession.NormalizeNote(request?.ReasonNote);
        if (note is null)
        {
            errors["reasonNote"] = [$"The reason must be {SupportSession.MinNoteLength} to {SupportSession.MaxNoteLength} characters."];
        }

        if (mode == SupportSessionMode.Write && (string.IsNullOrEmpty(request?.Password) || request.Password.Length > MaxPasswordLength))
        {
            errors["password"] = ["Enter your password to start a write session."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var householdId = request!.HouseholdId!.Value;
        // Households is the tenant root (not filtered): the one lookup that does not need a session.
        var household = await db.Households.AsNoTracking().Where(h => h.Id == householdId).Select(h => new { h.Id, h.Name }).FirstOrDefaultAsync(cancellationToken);
        if (household is null)
        {
            return AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.HouseholdNotFound, "Household not found", "There is no household with that id.");
        }

        if (mode == SupportSessionMode.Write && await CheckPasswordAsync(adminId, request.Password!) is { } refused)
        {
            return refused;
        }

        var options1 = options.CurrentValue;
        SupportSession? session = null;
        for (var attempt = 1; attempt <= MaxStartAttempts && session is null; attempt++)
        {
            try
            {
                session = await TryStartAsync(adminId, household.Id, household.Name, mode, category, note!, options1, cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception) && attempt < MaxStartAttempts)
            {
                // A parallel start of this admin won the unique index: read again, end that one, start ours.
                db.ChangeTracker.Clear();
            }
        }

        if (session is null)
        {
            return AppProblems.Problem(StatusCodes.Status409Conflict, "support-session-conflict", "Session conflict", "Another session start is in progress. Try again.");
        }

        scope.NotifyOwners = mode == SupportSessionMode.Write;
        return Results.Created((string?)null, ToResponse(session, household.Name));
    }

    private async Task<SupportSession> TryStartAsync(
        Guid adminId, Guid householdId, string householdName, SupportSessionMode mode, SupportReasonCategory category, string note,
        SupportOptions settings, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The previous session ends first and is saved on its own: the unique index allows one active row per admin.
        var previous = await db.SupportSessions.Where(s => s.AdminUserId == adminId && s.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.End(SupportEndedReasons.Replaced, now);
        }

        if (previous.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var session = SupportSession.Start(
            adminId, householdId, mode, category, note, now,
            TimeSpan.FromMinutes(settings.ReadSessionMinutes), TimeSpan.FromMinutes(settings.WriteSessionMinutes));
        db.SupportSessions.Add(session);
        scope.SessionId = session.Id;
        scope.TargetHouseholdId = householdId;
        scope.TargetHouseholdName = householdName;
        scope.Mode = mode;
        scope.ReasonCategory = category;
        scope.ReasonNote = note;
        scope.Detail("mode", mode.ToString());
        scope.Detail("replacedSessions", previous.Count);
        var entry = await audit.StageAsync(SupportActions.SessionStart, SupportAuditOutcome.Success, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        scope.Written = true;
        audit.Mirror(entry);
        return session;
    }

    /// <summary>The admin's own password for a write session. Null when it is right; otherwise the refusal.</summary>
    private async Task<IResult?> CheckPasswordAsync(Guid adminId, string password)
    {
        if (await users.FindByIdAsync(adminId.ToString()) is not { Email: { } email } adminUser)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        // The same per-email budget as sign-in, then the same lockout.
        if (!emailLimiter.TryAcquire(email.ToUpperInvariant(), out var retryAfter))
        {
            return new SupportRateLimitedResult(retryAfter);
        }

        var proof = await passwords.CheckAsync(adminUser, password);
        return proof.Outcome switch
        {
            PasswordProofOutcome.Ok => null,
            PasswordProofOutcome.Locked => new SupportRateLimitedResult(proof.RetryAfter),
            PasswordProofOutcome.NoPassword => AppProblems.Problem(
                StatusCodes.Status409Conflict, AppProblems.PasswordRequired, "No password set", "This account has no password, so a write session cannot be started."),
            _ => AppProblems.Problem(
                StatusCodes.Status403Forbidden, AppProblems.PasswordIncorrect, "The password is wrong", "Check the password and try again."),
        };
    }

    /// <summary>The admin's active, unexpired session as the API returns it, or the 404.</summary>
    public async Task<IResult> CurrentAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var adminId = user.UserId;
        var session = await db.SupportSessions.AsNoTracking()
            .Where(s => s.AdminUserId == adminId && s.EndedAt == null && s.ExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.SupportSessionRequired, "No support session", "There is no active support session.");
        }

        scope.UseSession(Info(session));
        var name = await db.Households.AsNoTracking().Where(h => h.Id == session.TargetHouseholdId).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(ToResponse(session, name ?? string.Empty));
    }

    /// <summary>Ends the admin's session. Idempotent: no session is 204 too.</summary>
    public async Task<IResult> EndAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var adminId = user.UserId;
        var open = await db.SupportSessions.Where(s => s.AdminUserId == adminId && s.EndedAt == null).ToListAsync(cancellationToken);
        var active = open.FirstOrDefault(s => s.IsActive(now));
        if (active is not null)
        {
            scope.UseSession(Info(active));
        }

        scope.Detail("hadSession", active is not null);
        foreach (var session in open)
        {
            session.End(SupportEndedReasons.Ended, now);
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static SupportSessionInfo Info(SupportSession s) =>
        new(s.Id, s.AdminUserId, s.TargetHouseholdId, s.Mode, s.ReasonCategory, s.ReasonNote, s.StartedAt, s.ExpiresAt);

    private static SupportSessionResponse ToResponse(SupportSession s, string householdName) =>
        new(s.Id, s.TargetHouseholdId, householdName, s.Mode, s.ReasonCategory, s.StartedAt, s.ExpiresAt);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}

/// <summary>429 with Retry-After, for a write session password that was tried too often. Counts as a refused request in the audit.</summary>
internal sealed class SupportRateLimitedResult(TimeSpan retryAfter) : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => StatusCodes.Status429TooManyRequests;

    public Task ExecuteAsync(HttpContext httpContext) => RateLimitingSetup.WriteRateLimitedAsync(httpContext, retryAfter);
}

/// <summary>Strict enum parsing for request fields: exact names (ignoring case), no numbers, no whitespace.</summary>
internal static class StrictEnum
{
    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}
