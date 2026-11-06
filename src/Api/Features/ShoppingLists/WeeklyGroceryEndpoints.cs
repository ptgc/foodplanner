using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// Add a weekly (regular) grocery. <c>Quantity</c> and <c>UnitId</c> go together, or neither (no amount). T-109: <c>Frequency</c>
/// is <c>Weekly</c> (default), <c>EveryTwoWeeks</c> or <c>Monthly</c>; <c>NextDue</c> (<c>ThisWeek</c> default, or <c>NextWeek</c>)
/// says which week an <c>EveryTwoWeeks</c> grocery starts in, and is rejected for the other frequencies.
/// </summary>
public sealed record WeeklyGroceryRequest(
    Guid? IngredientId, decimal? Quantity, Guid? UnitId, string? Frequency = null, string? NextDue = null);

/// <summary>
/// Change a weekly grocery (the ingredient stays). T-109: <c>Frequency</c> null keeps it; a DIFFERENT frequency resets the anchor
/// (every second week then starts in <c>NextDue</c>, <c>ThisWeek</c> by default); the same frequency keeps the anchor unless
/// <c>NextDue</c> moves an every-second-week grocery to this week or next week.
/// </summary>
public sealed record UpdateWeeklyGroceryRequest(decimal? Quantity, Guid? UnitId, string? Frequency = null, string? NextDue = null);

/// <summary>
/// <c>Name</c> is the ingredient's name in the request language; <c>Section</c> its store section (T-085). T-109: <c>Frequency</c>;
/// <c>StartWeek</c> is the anchor Monday of an every-second-week grocery (null otherwise); <c>NextDueWeek</c> is the first due
/// Monday from the current week (Europe/Oslo) on; <c>DueInWeek</c> is whether it is due in the <c>week</c> asked for (the current
/// week when the query is absent).
/// </summary>
public sealed record WeeklyGroceryResponse(
    Guid Id,
    Guid IngredientId,
    string Name,
    StoreSection Section,
    decimal? Quantity,
    Guid? UnitId,
    string? UnitCode,
    string? UnitName,
    GroceryFrequency Frequency,
    DateOnly? StartWeek,
    DateOnly NextDueWeek,
    bool DueInWeek);

/// <summary>
/// The household's weekly groceries (T-092). Any member reads (the shopping page needs to know whether the set is empty);
/// Adult or Owner manage the set (policy <c>HouseholdAdult</c>, no role string here). Putting the set on a list is
/// <c>POST /shopping-lists/{weekStart}/weekly</c> (any member), see <see cref="ShoppingListEndpoints"/>.
/// </summary>
public static class WeeklyGroceryEndpoints
{
    public static IEndpointRouteBuilder MapWeeklyGroceryEndpoints(this IEndpointRouteBuilder api)
    {
        var weekly = api.MapHouseholdScopedGroup().MapGroup("/weekly-groceries");

        weekly.MapGet("", ListWeeklyGroceries)
            .WithName("ListWeeklyGroceries")
            .Produces<WeeklyGroceryResponse[]>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        weekly.MapPost("", AddWeeklyGrocery)
            .WithName("AddWeeklyGrocery")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<WeeklyGroceryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        weekly.MapPut("/{id:guid}", UpdateWeeklyGrocery)
            .WithName("UpdateWeeklyGrocery")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<WeeklyGroceryResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        weekly.MapDelete("/{id:guid}", RemoveWeeklyGrocery)
            .WithName("RemoveWeeklyGrocery")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<IResult> ListWeeklyGroceries(
        string? week, RequestLocale locale, TimeProvider time, AppDbContext db, CancellationToken cancellationToken)
    {
        var current = GroceryCalendar.CurrentWeek(time);
        var reference = current;
        if (week is not null)
        {
            if (!ShoppingListEndpoints.IsWeekStart(week, out reference))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["week"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."],
                });
            }
        }

        var rows = await db.WeeklyGroceries.AsNoTracking().ToListAsync(cancellationToken);
        return Results.Ok(await ToResponsesAsync(db, rows, locale.Code, current, reference, cancellationToken));
    }

    private static async Task<IResult> AddWeeklyGrocery(
        WeeklyGroceryRequest? request, RequestLocale locale, TimeProvider time, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        GroceryFrequency? frequency = null;
        NextDueChoice? nextDue = null;
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            if (request.IngredientId is null || request.IngredientId == Guid.Empty)
            {
                errors["ingredientId"] = ["The ingredient is required."];
            }

            ValidateQuantity(request.Quantity, request.UnitId, errors);
            ParseSchedule(request.Frequency, request.NextDue, errors, out frequency, out nextDue);
            if (nextDue is not null && (frequency ?? GroceryFrequency.Weekly) != GroceryFrequency.EveryTwoWeeks)
            {
                errors["nextDue"] = ["The next due week can only be set for every second week."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Q-012 rule: ingredient and unit are resolved through the filtered sets before saving. Another household's id and an
        // unknown id get the same answer.
        var ingredientId = request!.IngredientId!.Value;
        if (!await db.Ingredients.AnyAsync(i => i.Id == ingredientId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ingredientId"] = ["The ingredient does not exist."] });
        }

        if (await UnitMissingAsync(db, request.UnitId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["unitId"] = ["The unit does not exist."] });
        }

        if (await db.WeeklyGroceries.AnyAsync(w => w.IngredientId == ingredientId, cancellationToken))
        {
            return Exists();
        }

        if (await db.WeeklyGroceries.CountAsync(cancellationToken) >= WeeklyGrocery.MaxPerHousehold)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.WeeklyGroceryLimit,
                "Too many weekly groceries",
                $"A household can have at most {WeeklyGrocery.MaxPerHousehold} weekly groceries.");
        }

        var currentWeek = GroceryCalendar.CurrentWeek(time);
        var chosen = frequency ?? GroceryFrequency.Weekly;
        var grocery = new WeeklyGrocery(
            ingredientId, request.Quantity, request.UnitId, chosen,
            chosen == GroceryFrequency.EveryTwoWeeks ? AnchorFor(nextDue, currentWeek) : null);
        db.WeeklyGroceries.Add(grocery);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two members added the same ingredient at once (unique index).
            db.ChangeTracker.Clear();
            return Exists();
        }

        var response = (await ToResponsesAsync(db, [grocery], locale.Code, currentWeek, currentWeek, cancellationToken)).Single();
        return Results.Created($"/api/households/{grocery.HouseholdId}/weekly-groceries", response);
    }

    private static async Task<IResult> UpdateWeeklyGrocery(
        Guid id, UpdateWeeklyGroceryRequest? request, RequestLocale locale, TimeProvider time, AppDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        GroceryFrequency? frequency = null;
        NextDueChoice? nextDue = null;
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            ValidateQuantity(request.Quantity, request.UnitId, errors);
            ParseSchedule(request.Frequency, request.NextDue, errors, out frequency, out nextDue);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await UnitMissingAsync(db, request!.UnitId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["unitId"] = ["The unit does not exist."] });
        }

        var grocery = await db.WeeklyGroceries.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (grocery is null)
        {
            return NotFound();
        }

        var currentWeek = GroceryCalendar.CurrentWeek(time);
        var target = frequency ?? grocery.Frequency;
        if (nextDue is not null && target != GroceryFrequency.EveryTwoWeeks)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["nextDue"] = ["The next due week can only be set for every second week."],
            });
        }

        grocery.SetQuantity(request.Quantity, request.UnitId);
        if (target != grocery.Frequency)
        {
            // A new frequency resets the anchor (Q-030): every second week starts this week unless told otherwise.
            grocery.SetSchedule(target, target == GroceryFrequency.EveryTwoWeeks ? AnchorFor(nextDue, currentWeek) : null);
        }
        else if (nextDue is not null)
        {
            grocery.SetSchedule(target, AnchorFor(nextDue, currentWeek)); // moved to this week or next week
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.WeeklyGroceryConflict,
                "Weekly grocery could not be saved",
                "The unit or ingredient of this weekly grocery was removed while saving. Reload and try again.");
        }

        return Results.Ok((await ToResponsesAsync(db, [grocery], locale.Code, currentWeek, currentWeek, cancellationToken)).Single());
    }

    /// <summary>Idempotent: a grocery that is gone is already removed. Lists that already hold it keep their items.</summary>
    private static async Task<IResult> RemoveWeeklyGrocery(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var grocery = await db.WeeklyGroceries.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (grocery is not null)
        {
            db.WeeklyGroceries.Remove(grocery);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // removed by someone else in between: gone either way
            }
        }

        return Results.NoContent();
    }

    /// <summary>Amount and unit go together (or neither); the amount is more than 0, at most 100000, at most 4 decimals.</summary>
    internal static void ValidateQuantity(decimal? quantity, Guid? unitId, Dictionary<string, string[]> errors)
    {
        if ((quantity is null) != (unitId is null))
        {
            errors["quantity"] = ["Give a quantity and a unit together, or neither."];
        }
        else if (quantity is { } q && !WeeklyGrocery.IsValidAmount(q))
        {
            errors["quantity"] = ["The quantity must be more than 0 and at most 100000, with at most 4 decimals."];
        }
    }

    /// <summary>Which week an every-second-week grocery starts in.</summary>
    private enum NextDueChoice
    {
        ThisWeek,
        NextWeek,
    }

    private static DateOnly AnchorFor(NextDueChoice? choice, DateOnly currentWeek) =>
        choice == NextDueChoice.NextWeek ? currentWeek.AddDays(7) : currentWeek;

    /// <summary>Names only (case-insensitive); numbers and unknown names are a 400 on the field.</summary>
    private static void ParseSchedule(
        string? frequency, string? nextDue, Dictionary<string, string[]> errors, out GroceryFrequency? parsedFrequency, out NextDueChoice? parsedNextDue)
    {
        parsedFrequency = null;
        parsedNextDue = null;
        if (frequency is not null)
        {
            if (Enum.GetValues<GroceryFrequency>().Select(v => (GroceryFrequency?)v)
                    .FirstOrDefault(v => string.Equals(v.ToString(), frequency, StringComparison.OrdinalIgnoreCase)) is { } f)
            {
                parsedFrequency = f;
            }
            else
            {
                errors["frequency"] = [$"The frequency must be one of {string.Join(", ", Enum.GetNames<GroceryFrequency>())}."];
            }
        }

        if (nextDue is not null)
        {
            if (Enum.GetValues<NextDueChoice>().Select(v => (NextDueChoice?)v)
                    .FirstOrDefault(v => string.Equals(v.ToString(), nextDue, StringComparison.OrdinalIgnoreCase)) is { } n)
            {
                parsedNextDue = n;
            }
            else
            {
                errors["nextDue"] = ["The next due week must be ThisWeek or NextWeek."];
            }
        }
    }

    private static async Task<bool> UnitMissingAsync(AppDbContext db, Guid? unitId, CancellationToken cancellationToken) =>
        unitId is { } id && !await db.Units.AnyAsync(u => u.Id == id, cancellationToken);

    /// <summary>By store section (walking order), then name in the request language, then id.</summary>
    private static async Task<WeeklyGroceryResponse[]> ToResponsesAsync(
        AppDbContext db, IReadOnlyList<WeeklyGrocery> rows, string locale, DateOnly currentWeek, DateOnly referenceWeek,
        CancellationToken cancellationToken)
    {
        var ingredientIds = rows.Select(r => r.IngredientId).Distinct().ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        var sections = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Section, cancellationToken);
        var units = await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale)
            .ToDictionaryAsync(u => u.Row.Id, u => (u.Row.Code, u.Name), cancellationToken);

        var responses = rows.Select(r => new WeeklyGroceryResponse(
            r.Id,
            r.IngredientId,
            names.GetValueOrDefault(r.IngredientId, string.Empty),
            sections.GetValueOrDefault(r.IngredientId, StoreSection.Other),
            r.Amount,
            r.UnitId,
            r.UnitId is { } u ? units.GetValueOrDefault(u).Code : null,
            r.UnitId is { } n ? units.GetValueOrDefault(n).Name : null,
            r.Frequency,
            r.StartWeek,
            r.NextDueFrom(currentWeek),
            r.IsDueIn(referenceWeek))).ToList();
        return [.. StoreSectionOrder.Sort(responses, r => r.Section, r => r.Name, ListComparer(locale), r => r.Id)];
    }

    private static StringComparer ListComparer(string locale) =>
        ShoppingListEndpoints.DisplayComparer(locale);

    private static IResult Exists() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.WeeklyGroceryExists,
            "Already a weekly grocery",
            "This ingredient is already one of the weekly groceries. Edit its amount instead.");

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.WeeklyGroceryNotFound, "Weekly grocery not found");
}
