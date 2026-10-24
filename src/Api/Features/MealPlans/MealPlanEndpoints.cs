using System.Globalization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.MealPlans;

/// <summary>Add a meal. <c>Servings</c> defaults to the recipe's servings (at most 50).</summary>
public sealed record AddMealPlanEntryRequest(DateOnly? Date, Guid? RecipeId, int? Servings);

/// <summary>Change an entry: the day (within the same week) and the servings. Both are required.</summary>
public sealed record UpdateMealPlanEntryRequest(DateOnly? Date, int? Servings);

public sealed record PickedByResponse(Guid UserId, string? Email);

public sealed record MealPlanEntryResponse(
    Guid Id, DateOnly Date, Guid RecipeId, string RecipeTitle, int Servings, PickedByResponse PickedBy);

/// <summary><c>Id</c> is null when nothing has been planned for the week yet (a read never creates a plan).</summary>
public sealed record MealPlanResponse(Guid? Id, DateOnly WeekStart, MealPlanEntryResponse[] Entries);

/// <summary>
/// Weekly meal plans (T-030). The plan of a week is addressed by the ISO-8601 Monday date. Any member reads and adds
/// entries; changing or deleting an entry needs Adult or Owner, or being the member who picked it (resource policy
/// <see cref="Policies.MealPlanEntryOwnerOrAdult"/>).
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
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
        return Results.Ok(new MealPlanResponse(
            plan?.Id, monday, plan is null ? [] : await LoadEntriesAsync(db, plan.Id, entryId: null, cancellationToken)));
    }

    private static async Task<IResult> AddEntry(
        Guid householdId, string weekStart, AddMealPlanEntryRequest? request, ICurrentUser user, AppDbContext db,
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

        var plan = await FindOrCreatePlanAsync(db, monday, cancellationToken);
        var servings = request!.Servings ?? Math.Min(recipe.Servings, MealPlanEntry.MaxServings);
        var entry = new MealPlanEntry(plan.Id, monday, request.Date!.Value, recipe.Id, servings, userId);
        db.MealPlanEntries.Add(entry);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var created = (await LoadEntriesAsync(db, plan.Id, entry.Id, cancellationToken)).Single();
        return Results.Created($"/api/households/{householdId}/meal-plans/{monday:yyyy-MM-dd}", created);
    }

    private static async Task<IResult> UpdateEntry(
        string weekStart, Guid entryId, UpdateMealPlanEntryRequest? request, IMealPlanEntryAccess access, AppDbContext db,
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

        if (!await access.CanChangeAsync(entry))
        {
            return Forbidden();
        }

        entry.Change(monday, request!.Date!.Value, request.Servings!.Value);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return Results.Ok((await LoadEntriesAsync(db, entry.MealPlanId, entry.Id, cancellationToken)).Single());
    }

    private static async Task<IResult> DeleteEntry(
        string weekStart, Guid entryId, IMealPlanEntryAccess access, AppDbContext db, CancellationToken cancellationToken)
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

        if (!await access.CanChangeAsync(entry))
        {
            return Forbidden();
        }

        db.MealPlanEntries.Remove(entry);
        return await TrySaveAsync(db, cancellationToken) ?? Results.NoContent();
    }

    private static void CheckServings(int? servings, Dictionary<string, string[]> errors)
    {
        if (servings is < MealPlanEntry.MinServings or > MealPlanEntry.MaxServings)
        {
            errors["servings"] = [$"Servings must be {MealPlanEntry.MinServings} to {MealPlanEntry.MaxServings}."];
        }
    }

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

    private static Task<MealPlanEntry?> FindEntryAsync(AppDbContext db, DateOnly monday, Guid entryId, CancellationToken cancellationToken) =>
        db.MealPlanEntries.FirstOrDefaultAsync(
            e => e.Id == entryId && db.MealPlans.Any(p => p.Id == e.MealPlanId && p.WeekStart == monday), cancellationToken);

    /// <summary>Entries by date, then in the order they were made. Pass <paramref name="entryId"/> for one entry.</summary>
    private static async Task<MealPlanEntryResponse[]> LoadEntriesAsync(
        AppDbContext db, Guid planId, Guid? entryId, CancellationToken cancellationToken)
    {
        var rows = await (from entry in db.MealPlanEntries.AsNoTracking()
                          join recipe in db.Recipes.AsNoTracking() on entry.RecipeId equals recipe.Id
                          join picker in db.Users.AsNoTracking() on entry.PickedByUserId equals picker.Id
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
                          }).ToListAsync(cancellationToken);
        return rows.Select(r => new MealPlanEntryResponse(
            r.Id, r.Date, r.RecipeId, r.RecipeTitle, r.Servings, new PickedByResponse(r.PickedByUserId, r.Email))).ToArray();
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

    private static IResult EntryNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.MealPlanEntryNotFound, "Meal plan entry not found");

    private static IResult Forbidden() =>
        AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
}
