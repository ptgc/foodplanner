using System.Globalization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Profiles;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.MealPlans;

/// <summary>Add a meal. <c>Servings</c> defaults to the recipe's servings (at most 50).</summary>
public sealed record AddMealPlanEntryRequest(DateOnly? Date, Guid? RecipeId, int? Servings);

/// <summary>Change an entry: the day (within the same week) and the servings. Both are required.</summary>
public sealed record UpdateMealPlanEntryRequest(DateOnly? Date, int? Servings);

public sealed record PickedByResponse(Guid UserId, string? Email);

/// <summary><c>PickedByProfile</c> is the kid profile that picked the entry (T-026), null when an account holder did or the profile was deleted.</summary>
public sealed record MealPlanEntryResponse(
    Guid Id, DateOnly Date, Guid RecipeId, string RecipeTitle, int Servings, PickedByResponse PickedBy, ProfileResponse? PickedByProfile);

/// <summary>
/// <c>Id</c> is null when nothing has been planned for the week yet (a read never creates a plan). <c>Locked</c> (T-026): an
/// Adult locked the week, so a Child or a kid profile cannot change it.
/// </summary>
public sealed record MealPlanResponse(
    Guid? Id, DateOnly WeekStart, MealPlanEntryResponse[] Entries, bool Locked, DateTimeOffset? LockedAt, PickedByResponse? LockedBy);

/// <summary>The week is locked: who and when.</summary>
public sealed record MealPlanLockResponse(bool Locked, DateTimeOffset LockedAt, PickedByResponse LockedBy);

/// <summary>
/// Weekly meal plans (T-030). The plan of a week is addressed by the ISO-8601 Monday date. Any member reads and adds
/// entries; changing or deleting an entry needs Adult or Owner, or being the member (in kid mode: the profile) who picked it
/// (resource policy <see cref="Policies.MealPlanEntryOwnerOrAdult"/>). An Adult can lock a week (T-026): then only Adult and
/// Owner can write to it (resource policy <see cref="Policies.MealPlanWeekWritable"/>, answered 409 <c>week-locked</c>).
/// Every write sends a content-free <c>MealPlanChanged</c> signal to the household's hub group after it is saved.
/// </summary>
public static class MealPlanEndpoints
{
    public static IEndpointRouteBuilder MapMealPlanEndpoints(this IEndpointRouteBuilder api)
    {
        var plans = api.MapHouseholdScopedGroup().MapGroup("/meal-plans/{weekStart}");

        plans.MapGet("", GetPlan)
            .WithName("GetMealPlan")
            .Produces<MealPlanResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        plans.MapPost("/entries", AddEntry)
            .WithName("AddMealPlanEntry")
            .Produces<MealPlanEntryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        plans.MapPut("/entries/{entryId:guid}", UpdateEntry)
            .WithName("UpdateMealPlanEntry")
            .Produces<MealPlanEntryResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        plans.MapDelete("/entries/{entryId:guid}", DeleteEntry)
            .WithName("DeleteMealPlanEntry")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        plans.MapPut("/lock", LockWeek)
            .WithName("LockMealPlanWeek")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<MealPlanLockResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        plans.MapDelete("/lock", UnlockWeek)
            .WithName("UnlockMealPlanWeek")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    /// <summary>The week of the route: a <c>yyyy-MM-dd</c> date that is a Monday.</summary>
    private static bool TryParseWeekStart(string value, Dictionary<string, string[]> errors, out DateOnly weekStart)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out weekStart)
            || !MealPlan.IsWeekStart(weekStart))
        {
            errors["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."];
            return false;
        }

        return true;
    }

    private static async Task<IResult> GetPlan(string weekStart, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var plan = await db.MealPlans.AsNoTracking().FirstOrDefaultAsync(p => p.WeekStart == monday, cancellationToken);
        if (plan is null)
        {
            return Results.Ok(new MealPlanResponse(null, monday, [], false, null, null));
        }

        var entries = await LoadEntriesAsync(db, plan.Id, entryId: null, cancellationToken);
        return Results.Ok(new MealPlanResponse(plan.Id, monday, entries, plan.IsLocked, plan.LockedAt, await LockerAsync(db, plan, cancellationToken)));
    }

    private static async Task<IResult> AddEntry(
        Guid householdId, string weekStart, AddMealPlanEntryRequest? request, ICurrentUser user, AppDbContext db,
        IMealPlanWeekAccess week, IKidPickRules kidRules, IMealPlanNotifier notifier, ILogger<MealPlan> logger,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            if (request.Date is not { } date || !MealPlan.IsInWeek(monday, date))
            {
                errors["date"] = ["The date must be a day of the plan's week (Monday to Sunday)."];
            }

            if (request.RecipeId is null || request.RecipeId == Guid.Empty)
            {
                errors["recipeId"] = ["A recipe is required."];
            }

            CheckServings(request.Servings, errors);
        }

        if (errors.Count > 0 || user.UserId is not { } userId)
        {
            return Results.ValidationProblem(errors);
        }

        // Q-012 rule: the recipe is resolved through the filtered set. Another household's recipe is "not found", with
        // the same answer as an unknown id, and nothing is written.
        var recipeId = request!.RecipeId;
        var recipe = await db.Recipes.AsNoTracking().Where(r => r.Id == recipeId)
            .Select(r => new { r.Id, r.Servings }).FirstOrDefaultAsync(cancellationToken);
        if (recipe is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["recipeId"] = ["The recipe does not exist."] });
        }

        // In kid mode the pick is attributed to the active profile (from the cookie claim, never the body), and a kid may only
        // add what IKidPickRules allows (T-026: dinners; T-090 extends it).
        Guid? profileId = null;
        if (user.IsKidMode)
        {
            if (!await KidMayAddAsync(db, kidRules, monday, request.Date!.Value, recipe.Id, cancellationToken))
            {
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.PickNotAllowed,
                    "That meal cannot be picked",
                    "Kids can pick dinners for the week plan.");
            }

            profileId = await ActiveProfileIdAsync(householdId, user, db, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await FindOrCreatePlanAsync(db, monday, cancellationToken);

        // The plan row is taken for the rest of the transaction and re-read, so a lock that commits before this write is seen,
        // and one that comes after waits for it: no write by a Child or kid passes a lock that has been answered.
        await LockPlanRowAsync(db, plan, cancellationToken);
        if (!await week.CanWriteAsync(plan))
        {
            return WeekLocked();
        }

        var servings = request.Servings ?? Math.Min(recipe.Servings, MealPlanEntry.MaxServings);
        var entry = new MealPlanEntry(plan.Id, monday, request.Date!.Value, recipe.Id, servings, userId, profileId);
        db.MealPlanEntries.Add(entry);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.EntryAdded, entry.Id), cancellationToken);

        var created = (await LoadEntriesAsync(db, plan.Id, entry.Id, cancellationToken)).Single();
        return Results.Created($"/api/households/{householdId}/meal-plans/{monday:yyyy-MM-dd}", created);
    }

    private static async Task<IResult> UpdateEntry(
        Guid householdId, string weekStart, Guid entryId, UpdateMealPlanEntryRequest? request, IMealPlanEntryAccess access,
        IMealPlanWeekAccess week, AppDbContext db, IMealPlanNotifier notifier, ILogger<MealPlan> logger, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            if (request.Date is not { } date || !MealPlan.IsInWeek(monday, date))
            {
                errors["date"] = ["The date must be a day of the plan's week (Monday to Sunday)."];
            }

            if (request.Servings is null)
            {
                errors["servings"] = ["Servings are required."];
            }

            CheckServings(request.Servings, errors);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var entry = await FindEntryAsync(db, monday, entryId, cancellationToken);
        if (entry is null)
        {
            return EntryNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.MealPlans.FirstAsync(p => p.Id == entry.MealPlanId, cancellationToken);
        await LockPlanRowAsync(db, plan, cancellationToken);

        // The week first, then the entry's owner: a kid editing anyone's pick in a locked week gets week-locked.
        if (!await week.CanWriteAsync(plan))
        {
            return WeekLocked();
        }

        if (!await access.CanChangeAsync(entry))
        {
            return Forbidden();
        }

        entry.Change(monday, request!.Date!.Value, request.Servings!.Value);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.EntryChanged, entry.Id), cancellationToken);
        return Results.Ok((await LoadEntriesAsync(db, entry.MealPlanId, entry.Id, cancellationToken)).Single());
    }

    private static async Task<IResult> DeleteEntry(
        Guid householdId, string weekStart, Guid entryId, IMealPlanEntryAccess access, IMealPlanWeekAccess week, AppDbContext db,
        IMealPlanNotifier notifier, ILogger<MealPlan> logger, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var entry = await FindEntryAsync(db, monday, entryId, cancellationToken);
        if (entry is null)
        {
            return EntryNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.MealPlans.FirstAsync(p => p.Id == entry.MealPlanId, cancellationToken);
        await LockPlanRowAsync(db, plan, cancellationToken);

        if (!await week.CanWriteAsync(plan))
        {
            return WeekLocked();
        }

        if (!await access.CanChangeAsync(entry))
        {
            return Forbidden();
        }

        db.MealPlanEntries.Remove(entry);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        await NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.EntryRemoved, entryId), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Locks the week (Adult or Owner). Locking a locked week is 200 and keeps the first locker and time. A week with no plan
    /// yet gets an empty plan row, as the first entry would create it.
    /// </summary>
    private static async Task<IResult> LockWeek(
        Guid householdId, string weekStart, ICurrentUser user, AppDbContext db, TimeProvider time, IMealPlanNotifier notifier,
        ILogger<MealPlan> logger, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await FindOrCreatePlanAsync(db, monday, cancellationToken);
        await LockPlanRowAsync(db, plan, cancellationToken);
        var changed = !plan.IsLocked;
        plan.Lock(time.GetUtcNow(), userId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (changed)
        {
            await NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.Locked, null), cancellationToken);
        }

        var locker = await LockerAsync(db, plan, cancellationToken);
        return Results.Ok(new MealPlanLockResponse(true, plan.LockedAt!.Value, locker!));
    }

    /// <summary>Unlocks the week (Adult or Owner). Idempotent: an unlocked or missing plan is just 204.</summary>
    private static async Task<IResult> UnlockWeek(
        Guid householdId, string weekStart, AppDbContext db, IMealPlanNotifier notifier, ILogger<MealPlan> logger,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var plan = await db.MealPlans.FirstOrDefaultAsync(p => p.WeekStart == monday, cancellationToken);
        if (plan is null)
        {
            return Results.NoContent();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPlanRowAsync(db, plan, cancellationToken);
        var changed = plan.IsLocked;
        plan.Unlock();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (changed)
        {
            await NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.Unlocked, null), cancellationToken);
        }

        return Results.NoContent();
    }

    private static void CheckServings(int? servings, Dictionary<string, string[]> errors)
    {
        if (servings is < MealPlanEntry.MinServings or > MealPlanEntry.MaxServings)
        {
            errors["servings"] = [$"Servings must be {MealPlanEntry.MinServings} to {MealPlanEntry.MaxServings}."];
        }
    }

    /// <summary>Asks the kid pick rules (the seam T-090 extends) whether the recipe may be added on that day of that week.</summary>
    private static async Task<bool> KidMayAddAsync(
        AppDbContext db, IKidPickRules rules, DateOnly monday, DateOnly day, Guid recipeId, CancellationToken cancellationToken)
    {
        var categories = await (from link in db.RecipeTags.AsNoTracking()
                                join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                                where link.RecipeId == recipeId && tag.Kind == TagKind.Category
                                select tag.Id).ToListAsync(cancellationToken);
        var planned = await (from entry in db.MealPlanEntries.AsNoTracking()
                             join plan in db.MealPlans.AsNoTracking() on entry.MealPlanId equals plan.Id
                             where plan.WeekStart == monday
                             select new { entry.Date, entry.RecipeId }).ToListAsync(cancellationToken);
        return rules.IsAllowed(
            new KidPickRecipe(recipeId, categories.ToHashSet()),
            day,
            new KidPickPlan(monday, [.. planned.Select(p => (p.Date, p.RecipeId))]));
    }

    /// <summary>The active profile when it still exists in this household; null otherwise (a stale claim attributes to nobody).</summary>
    private static async Task<Guid?> ActiveProfileIdAsync(Guid householdId, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken) =>
        user is { ActiveProfileId: { } profileId, ActiveProfileHouseholdId: { } profileHousehold }
        && profileHousehold == householdId
        && await db.MemberProfiles.AsNoTracking().AnyAsync(p => p.Id == profileId, cancellationToken)
            ? profileId
            : null;

    /// <summary>
    /// The plan of the week, created on the first entry. Two members adding at once can both try to create it; the unique
    /// (household, week) index lets one win and the other reads the winner's plan.
    /// </summary>
    private static async Task<MealPlan> FindOrCreatePlanAsync(AppDbContext db, DateOnly monday, CancellationToken cancellationToken)
    {
        var plan = await db.MealPlans.FirstOrDefaultAsync(p => p.WeekStart == monday, cancellationToken);
        if (plan is not null)
        {
            return plan;
        }

        plan = new MealPlan(monday);
        db.MealPlans.Add(plan);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return plan;
        }
        catch (DbUpdateException)
        {
            db.Entry(plan).State = EntityState.Detached;
            return await db.MealPlans.FirstOrDefaultAsync(p => p.WeekStart == monday, cancellationToken) ?? throw new InvalidOperationException(
                "The meal plan could not be created and does not exist.");
        }
    }

    /// <summary>
    /// Takes the plan row for the rest of the transaction (a tracked no-op update of the audit column, the same lock pattern as
    /// the household row in T-048) and re-reads it, so <see cref="MealPlan.LockedAt"/> is what is committed NOW. Only the audit
    /// column is written, never a possibly stale lock state.
    /// </summary>
    private static async Task LockPlanRowAsync(AppDbContext db, MealPlan plan, CancellationToken cancellationToken)
    {
        db.Entry(plan).Property("UpdatedAt").IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(plan).ReloadAsync(cancellationToken);
    }

    private static Task<MealPlanEntry?> FindEntryAsync(AppDbContext db, DateOnly monday, Guid entryId, CancellationToken cancellationToken) =>
        db.MealPlanEntries.FirstOrDefaultAsync(
            e => e.Id == entryId && db.MealPlans.Any(p => p.Id == e.MealPlanId && p.WeekStart == monday), cancellationToken);

    private static async Task<PickedByResponse?> LockerAsync(AppDbContext db, MealPlan plan, CancellationToken cancellationToken)
    {
        if (plan.LockedByUserId is not { } lockerId)
        {
            return null;
        }

        var email = await db.Users.AsNoTracking().Where(u => u.Id == lockerId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        return new PickedByResponse(lockerId, email);
    }

    /// <summary>Entries by date, then in the order they were made. Pass <paramref name="entryId"/> for one entry.</summary>
    private static async Task<MealPlanEntryResponse[]> LoadEntriesAsync(
        AppDbContext db, Guid planId, Guid? entryId, CancellationToken cancellationToken)
    {
        var rows = await (from entry in db.MealPlanEntries.AsNoTracking()
                          join recipe in db.Recipes.AsNoTracking() on entry.RecipeId equals recipe.Id
                          join picker in db.Users.AsNoTracking() on entry.PickedByUserId equals picker.Id
                          join profile in db.MemberProfiles.AsNoTracking() on entry.PickedByProfileId equals profile.Id into profiles
                          from profile in profiles.DefaultIfEmpty()
                          where entry.MealPlanId == planId && (entryId == null || entry.Id == entryId)
                          orderby entry.Date, EF.Property<DateTimeOffset>(entry, "CreatedAt"), entry.Id
                          select new
                          {
                              entry.Id,
                              entry.Date,
                              entry.RecipeId,
                              RecipeTitle = recipe.Title,
                              entry.Servings,
                              entry.PickedByUserId,
                              picker.Email,
                              ProfileId = (Guid?)profile.Id,
                              ProfileName = profile.DisplayName,
                              ProfileAvatar = profile.Avatar,
                          }).ToListAsync(cancellationToken);
        return rows.Select(r => new MealPlanEntryResponse(
            r.Id, r.Date, r.RecipeId, r.RecipeTitle, r.Servings, new PickedByResponse(r.PickedByUserId, r.Email),
            r.ProfileId is { } id ? new ProfileResponse(id, r.ProfileName, r.ProfileAvatar) : null)).ToArray();
    }

    /// <summary>A <see cref="DbUpdateConcurrencyException"/> (a concurrent delete, or a forged entity) is a 409, not a 500. Null when saved.</summary>
    private static async Task<IResult?> TrySaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.MealPlanEntryConflict,
                "Meal plan entry changed",
                "The entry was changed or deleted by someone else. Reload the plan and try again.");
        }
    }

    /// <summary>After the commit: a failing notifier is logged and never undoes the change.</summary>
    private static async Task NotifyAsync(
        IMealPlanNotifier notifier, ILogger<MealPlan> logger, MealPlanChange change, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.NotifyAsync(change, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not announce meal plan change {Kind} for week {WeekStart}.", change.Kind, change.WeekStart);
        }
    }

    private static IResult EntryNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.MealPlanEntryNotFound, "Meal plan entry not found");

    private static IResult Forbidden() =>
        AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");

    private static IResult WeekLocked() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.WeekLocked,
            "The week is locked",
            "An adult has locked this week. Only an adult can change it.");
}
