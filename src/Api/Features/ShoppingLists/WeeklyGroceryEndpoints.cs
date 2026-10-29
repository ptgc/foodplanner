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

/// <summary>Add or change a weekly grocery. <c>Quantity</c> and <c>UnitId</c> go together, or neither (no amount).</summary>
public sealed record WeeklyGroceryRequest(Guid? IngredientId, decimal? Quantity, Guid? UnitId);

/// <summary>Change the amount of a weekly grocery (the ingredient stays).</summary>
public sealed record UpdateWeeklyGroceryRequest(decimal? Quantity, Guid? UnitId);

/// <summary><c>Name</c> is the ingredient's name in the request language; <c>Section</c> its store section (T-085).</summary>
public sealed record WeeklyGroceryResponse(
    Guid Id, Guid IngredientId, string Name, StoreSection Section, decimal? Quantity, Guid? UnitId, string? UnitCode, string? UnitName);

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

    private static async Task<IResult> ListWeeklyGroceries(RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.WeeklyGroceries.AsNoTracking().ToListAsync(cancellationToken);
        return Results.Ok(await ToResponsesAsync(db, rows, locale.Code, cancellationToken));
    }

    private static async Task<IResult> AddWeeklyGrocery(
        WeeklyGroceryRequest? request, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
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

        var grocery = new WeeklyGrocery(ingredientId, request.Quantity, request.UnitId);
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

        var response = (await ToResponsesAsync(db, [grocery], locale.Code, cancellationToken)).Single();
        return Results.Created($"/api/households/{grocery.HouseholdId}/weekly-groceries", response);
    }

    private static async Task<IResult> UpdateWeeklyGrocery(
        Guid id, UpdateWeeklyGroceryRequest? request, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            ValidateQuantity(request.Quantity, request.UnitId, errors);
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

        grocery.SetQuantity(request.Quantity, request.UnitId);
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
                "Weekly groceries changed",
                "The weekly groceries were changed by someone else at the same time. Reload and try again.");
        }

        return Results.Ok((await ToResponsesAsync(db, [grocery], locale.Code, cancellationToken)).Single());
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

    private static async Task<bool> UnitMissingAsync(AppDbContext db, Guid? unitId, CancellationToken cancellationToken) =>
        unitId is { } id && !await db.Units.AnyAsync(u => u.Id == id, cancellationToken);

    /// <summary>By store section (walking order), then name in the request language, then id.</summary>
    private static async Task<WeeklyGroceryResponse[]> ToResponsesAsync(
        AppDbContext db, IReadOnlyList<WeeklyGrocery> rows, string locale, CancellationToken cancellationToken)
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
            r.UnitId is { } n ? units.GetValueOrDefault(n).Name : null)).ToList();
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
