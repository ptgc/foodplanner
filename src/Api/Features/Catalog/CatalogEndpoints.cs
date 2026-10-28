using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Catalog;

/// <summary><c>Section</c> (T-085) is a <see cref="StoreSection"/> name; omitted means Other.</summary>
public sealed record CreateIngredientRequest(string? Name, bool? IsStaple, string? Dimension, string? Section = null);

/// <summary><c>IsGlobal</c>: a catalog ingredient shared by every household (read-only); otherwise the household's own.</summary>
public sealed record IngredientResponse(Guid Id, string Name, bool IsStaple, UnitDimension Dimension, bool IsGlobal, StoreSection Section);

public sealed record UnitResponse(Guid Id, string Code, string Name, UnitDimension Dimension, decimal FactorToBase);

/// <summary>Ingredient catalog (global rows plus the household's own) and the global units (T-020).</summary>
public static class CatalogEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        // Units are global data, readable by any signed-in user (the API-wide fallback policy requires authentication).
        api.MapGet("/units", ListUnits)
            .WithName("ListUnits")
            .Produces<UnitResponse[]>();

        // Household-scoped (Q-004). Any member may read; only an Adult or Owner may create.
        var ingredients = api.MapHouseholdScopedGroup().MapGroup("/ingredients");
        ingredients.MapGet("", ListIngredients)
            .WithName("ListIngredients")
            .Produces<IngredientResponse[]>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ingredients.MapPost("", CreateIngredient)
            .WithName("CreateIngredient")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<IngredientResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>Units with their name in the request language (Accept-Language); the code is the stable identity and is not translated.</summary>
    private static async Task<IResult> ListUnits(RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var units = await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale.Code)
            .OrderBy(u => u.Row.Dimension).ThenBy(u => u.Row.FactorToBase).ThenBy(u => u.Row.Code).ToListAsync(cancellationToken);
        return Results.Ok(units.Select(u => new UnitResponse(u.Row.Id, u.Row.Code, u.Name, u.Row.Dimension, u.Row.FactorToBase)).ToArray());
    }

    /// <summary>
    /// Global ingredients plus this household's, filtered by the query filter (the household comes from the route, never
    /// from the query string). <c>search</c> is a case-insensitive "contains" on the name in the request language
    /// (Accept-Language; a household's own ingredient has the one name it was typed with); the result is capped at <c>limit</c>
    /// (default 50, at most 100) and sorted by that name with the language's collation, then by id so paging is stable.
    /// </summary>
    private static async Task<IResult> ListIngredients(string? search, int? limit, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var term = search?.Trim();
        if (term is { Length: > Ingredient.MaxNameLength })
        {
            errors["search"] = [$"The search text can be at most {Ingredient.MaxNameLength} characters."];
        }

        if (limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"The limit must be 1 to {MaxLimit}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Ingredients.AsNoTracking().WithNames(db, CatalogTranslation.KindIngredient, locale.Code);
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(i => i.Name.Contains(term));
        }

        var rows = await query.OrderByName(locale.Code).Take(limit ?? DefaultLimit).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(r => ToResponse(r.Row, r.Name)).ToArray());
    }

    /// <summary>
    /// Creates the household's own ingredient. The name must be new among everything the household can see: its own and
    /// the global catalog (a household does not shadow a global name) in every language (the English name on the row and
    /// each translation), compared case-insensitively by the database collation. A duplicate is 409. A global ingredient cannot be created or changed here: only the seed writes those.
    /// </summary>
    private static async Task<IResult> CreateIngredient(
        CreateIngredientRequest? request, ICurrentHousehold household, AppDbContext db, CancellationToken cancellationToken)
    {
        // The policy guarantees a household context; a missing one is a wiring bug, so refuse rather than guess.
        if (household.HouseholdId is not { } householdId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        var errors = new Dictionary<string, string[]>();
        var name = Ingredient.NormalizeName(request?.Name);
        if (name is null)
        {
            errors["name"] = [$"The name must be 1 to {Ingredient.MaxNameLength} characters, without control characters."];
        }

        var dimension = default(UnitDimension);
        if (!UnitDimensions.TryParse(request?.Dimension, out dimension))
        {
            errors["dimension"] = ["The dimension must be Mass, Volume or Count."];
        }

        var section = StoreSection.Other;
        if (request?.Section is not null && !StoreSections.TryParse(request.Section, out section))
        {
            errors["section"] = ["The section must be FruitAndVegetables, MeatFishAndCheese, Frozen, Dairy, DryGoods or Other."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.Ingredients.AnyAsync(i => i.Name == name, cancellationToken)
            || await db.AnyGlobalNameAsync(CatalogTranslation.KindIngredient, name!, cancellationToken))
        {
            return Duplicate(name!);
        }

        var ingredient = new Ingredient(householdId, name!, request?.IsStaple ?? false, dimension, section);
        db.Ingredients.Add(ingredient);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // A concurrent request created the same name in this household between the check and the insert.
            return Duplicate(name!);
        }

        return Results.Created((string?)null, ToResponse(ingredient, ingredient.Name));
    }

    private static IResult Duplicate(string name) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.IngredientExists,
            "Ingredient already exists",
            $"There is already an ingredient called '{name}' in this household or in the shared catalog.");

    private static IngredientResponse ToResponse(Ingredient i, string name) => new(i.Id, name, i.IsStaple, i.Dimension, i.HouseholdId is null, i.Section);
}
