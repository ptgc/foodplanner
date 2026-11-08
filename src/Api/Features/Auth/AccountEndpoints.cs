using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Households;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Realtime;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Foodplanner.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Auth;

/// <summary>A household named in the deletion preview or in a refusal.</summary>
public sealed record AccountHouseholdResponse(Guid Id, string Name, int MemberCount);

/// <summary>
/// What deleting the signed-in user's account would do (T-095). <c>householdsToDelete</c>: the user is the only member, so
/// they are deleted with the account. <c>blockingHouseholds</c>: the user is the only Owner and others are members; while there
/// are any, <c>canDelete</c> is false. <c>householdsKept</c>: they live on without the user. <c>confirmationText</c> is what the
/// person must type (their email address).
/// </summary>
public sealed record AccountDeletionPreviewResponse(
    bool CanDelete,
    string ConfirmationText,
    AccountHouseholdResponse[] HouseholdsToDelete,
    AccountHouseholdResponse[] BlockingHouseholds,
    AccountHouseholdResponse[] HouseholdsKept);

/// <summary>
/// <c>Password</c>: the account password, entered again. <c>Confirmation</c>: the account's email address, typed.
/// <c>HouseholdIds</c>: the ids of <c>householdsToDelete</c> the person was shown; if the set changed since, the request is
/// refused (409) instead of deleting something they did not see.
/// </summary>
public sealed record DeleteAccountRequest(string? Password, string? Confirmation, Guid[]? HouseholdIds);

/// <summary>
/// Deleting your own account (T-095, Q-027). Account-level, not household-scoped: <see cref="Policies.FullSession"/> keeps a
/// device in kid mode out. Cross-household reads and writes go through <see cref="ITenantResolution"/>, the one place that
/// may bypass the tenant filter; this file never does.
/// </summary>
public static class AccountEndpoints
{
    private const int MaxPasswordLength = 128;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder api)
    {
        var account = api.MapGroup("/account").RequireAuthorization(Policies.FullSession);

        account.MapGet("/deletion-preview", Preview)
            .WithName("GetAccountDeletionPreview")
            .Produces<AccountDeletionPreviewResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        account.MapPost("/delete", Delete)
            .RequireRateLimiting(RateLimitPolicies.AccountDelete)
            .WithName("DeleteAccount")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> Preview(
        ICurrentUser current, UserManager<AppUser> users, ITenantResolution tenants, CancellationToken cancellationToken)
    {
        if (current.UserId is not { } userId || await users.FindByIdAsync(userId.ToString()) is not { Email: { } email })
        {
            return Unauthenticated();
        }

        var plan = AccountDeletionPlan.Create(await tenants.GetHouseholdFactsForUser(userId, cancellationToken));
        return Results.Ok(new AccountDeletionPreviewResponse(
            plan.CanDelete, email, ToResponses(plan.ToDelete), ToResponses(plan.Blocking), ToResponses(plan.Remaining)));
    }

    private static async Task<IResult> Delete(
        DeleteAccountRequest? request,
        ICurrentUser current,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        PasswordProof passwords,
        LoginEmailRateLimiter emailLimiter,
        ITenantResolution tenants,
        AppDbContext db,
        IImageStore images,
        ILiveConnections live,
        TimeProvider time,
        ILogger<AppUser> logger,
        CancellationToken cancellationToken)
    {
        if (current.UserId is not { } userId || await users.FindByIdAsync(userId.ToString()) is not { Email: { } email } user)
        {
            return Unauthenticated();
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(request?.Password) || request.Password.Length > MaxPasswordLength)
        {
            errors["password"] = ["Enter your password."];
        }

        if (!AccountDeletionConfirmation.Matches(request?.Confirmation, email))
        {
            errors["confirmation"] = ["Type your email address to confirm."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Same per-email budget as sign-in (shared with it), then the same lockout (PasswordProof).
        if (!emailLimiter.TryAcquire(email.ToUpperInvariant(), out var retryAfter))
        {
            return RateLimited(retryAfter);
        }

        var proof = await passwords.CheckAsync(user, request!.Password!);
        switch (proof.Outcome)
        {
            case PasswordProofOutcome.Locked:
                return RateLimited(proof.RetryAfter);
            case not PasswordProofOutcome.Ok:
                return AppProblems.Problem(
                    StatusCodes.Status403Forbidden, AppProblems.PasswordIncorrect, "The password is wrong", "Check the password and try again.");
        }

        var now = time.GetUtcNow();
        var deletedHouseholds = new List<Guid>();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // First statement: serialise every delete of this account on the user's own row (a no-op update takes the row
            // lock until the end of the transaction). A double submit queues here and then finds the user gone: 401.
            if (!await tenants.LockUser(userId, cancellationToken))
            {
                return Unauthenticated();
            }

            // One plain read gives the lock order (id order, so two deletions cannot deadlock). The households are locked and
            // the plan is read only then: a leave, a role change or a removal in one of them (they take the same lock) cannot
            // slip in between.
            var order = await tenants.GetHouseholdFactsForUser(userId, cancellationToken);
            foreach (var id in order.Select(h => h.HouseholdId).Order())
            {
                await HouseholdManagementEndpoints.LockHouseholdAsync(db, id, cancellationToken);
            }

            var plan = AccountDeletionPlan.Create(await tenants.GetHouseholdFactsForUser(userId, cancellationToken));
            if (!plan.CanDelete)
            {
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.AccountOwnsHouseholds,
                    "You are the only Owner of a household with other members",
                    "Transfer ownership to another member first, or remove the other members.",
                    new Dictionary<string, object?> { ["households"] = ToResponses(plan.Blocking) });
            }

            if (!plan.ToDeleteIs(request.HouseholdIds ?? []))
            {
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.AccountDeletionChanged,
                    "The households that would be deleted have changed",
                    "Look at the list again and confirm it.",
                    new Dictionary<string, object?> { ["households"] = ToResponses(plan.ToDelete) });
            }

            // Households where the user is the only member go the way DELETE /households/{h} takes: Household is the tenant
            // root, and one DELETE statement cascades to everything it owns.
            foreach (var facts in plan.ToDelete)
            {
                var household = await db.Households.SingleAsync(h => h.Id == facts.HouseholdId, cancellationToken);
                db.Households.Remove(household);
                deletedHouseholds.Add(facts.HouseholdId);
            }

            await db.SaveChangesAsync(cancellationToken);

            var erased = await tenants.EraseUserReferences(userId, now, cancellationToken);
            logger.LogInformation(
                "Deleting account {UserId}: {Households} household(s) deleted, {Picks} picks, {Checks} checked items, {Pantry} pantry rows, {Invites} invites revoked, {Drafts} drafts deleted.",
                userId, deletedHouseholds.Count, erased.PicksCleared, erased.CheckedItemsCleared, erased.PantryConfirmationsCleared, erased.InvitesRevoked, erased.ImportDraftsDeleted);

            // Memberships, the PIN, claims, logins and tokens cascade from the user row.
            var deleted = await users.DeleteAsync(user);
            if (!deleted.Succeeded)
            {
                // Rolled back by leaving the transaction uncommitted. The account changed under us (a parallel request).
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict, AppProblems.AccountDeletionChanged, "The account changed while it was being deleted", "Try again.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsDeadlock(exception))
        {
            // Backstop: the locks above serialise two deletes, but a lock taken by another feature in a different order can
            // still make SQL Server pick this request as victim. Everything rolled back (the transaction was disposed).
            return AppProblems.Problem(
                StatusCodes.Status409Conflict, AppProblems.AccountDeletionChanged, "The account changed while it was being deleted", "Try again.");
        }

        // After the commit, best effort: files of the deleted households (invisible orphans if this fails), live connections,
        // and this browser's cookie. Other devices' cookies stop working once the security stamp is next validated (the user
        // no longer exists); until then there is no account behind them.
        foreach (var householdId in deletedHouseholds)
        {
            await RecipeImageEndpoints.TryDeleteBlobsAsync(images, logger, RecipeImageBlobNames.HouseholdPrefix(householdId), CancellationToken.None);
            await live.CloseHouseholdAsync(householdId);
        }

        await live.CloseUserAsync(userId);
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static AccountHouseholdResponse[] ToResponses(IEnumerable<AccountHouseholdFacts> households) =>
        [.. households.Select(h => new AccountHouseholdResponse(h.HouseholdId, h.Name, h.MemberCount))];

    private const int SqlDeadlockVictim = 1205;

    private static bool IsDeadlock(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is Microsoft.Data.SqlClient.SqlException { Number: SqlDeadlockVictim })
            {
                return true;
            }
        }

        return false;
    }

    private static IResult RateLimited(TimeSpan retryAfter) => new RateLimitedResult(retryAfter);

    private static IResult Unauthenticated() =>
        AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");

    private sealed class RateLimitedResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => RateLimitingSetup.WriteRateLimitedAsync(httpContext, retryAfter);
    }
}
