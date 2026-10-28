using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Catalog;

public sealed record RenameIngredientRequest(string? Name);

public sealed record MergeIngredientRequest(Guid? TargetIngredientId);

/// <summary>Where an ingredient is used: distinct recipes, pantry rows (0 or 1), shopping lists (weeks) and open import drafts.</summary>
public sealed record IngredientUsageResponse(int Recipes, int PantryItems, int ShoppingLists, int ImportDrafts);

public sealed record OwnIngredientResponse(Guid Id, string Name, bool IsStaple, UnitDimension Dimension, IngredientUsageResponse Usage);

/// <summary>
/// <c>Warnings</c> are codes the client translates: <c>dimension-differs</c> (the target is normally measured in another
/// dimension, so quantities of the two may stay separate lines on a shopping list) and <c>staple-differs</c>.
/// </summary>
public sealed record MergeIngredientResponse(IngredientResponse Target, IngredientUsageResponse Moved, string[] Warnings);

/// <summary>
/// The foreign keys to <c>Ingredient</c> that merge moves (and that block a delete). A test derives the list of foreign keys
/// to Ingredient from the EF model and fails if one is missing here, so a new reference (T-076 alternatives) cannot be forgotten.
/// </summary>
public static class IngredientReferences
{
    public static readonly (Type Dependent, string Property)[] Handled =
    [
        (typeof(Foodplanner.Domain.Recipes.RecipeIngredient), nameof(Foodplanner.Domain.Recipes.RecipeIngredient.IngredientId)),
        (typeof(Foodplanner.Domain.ShoppingLists.ShoppingListItem), nameof(Foodplanner.Domain.ShoppingLists.ShoppingListItem.IngredientId)),
        (typeof(Foodplanner.Domain.ShoppingLists.PantryItem), nameof(Foodplanner.Domain.ShoppingLists.PantryItem.IngredientId)),
    ];
}

/// <summary>
/// Managing the household's own ingredients (T-079): list with usage, rename, delete and merge. Adult or Owner only.
/// Global catalog rows are read-only: addressing one answers <c>403 ingredient-global</c>; another household's own row
/// is invisible through the tenant filter and answers <c>404 ingredient-not-found</c>. See docs/domain.md.
/// </summary>
public static class OwnIngredientEndpoints
{
    public static IEndpointRouteBuilder MapOwnIngredientEndpoints(this IEndpointRouteBuilder api)
    {
        var ingredients = api.MapHouseholdScopedGroup().MapGroup("/ingredients");

        ingredients.MapGet("/own", ListOwn)
            .WithName("ListOwnIngredients")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<OwnIngredientResponse[]>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ingredients.MapPut("/{ingredientId:guid}", Rename)
            .WithName("RenameIngredient")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<IngredientResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        ingredients.MapDelete("/{ingredientId:guid}", Delete)
            .WithName("DeleteIngredient")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        ingredients.MapPost("/{ingredientId:guid}/merge", Merge)
            .WithName("MergeIngredient")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<MergeIngredientResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    // ---- List ------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListOwn(string? search, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var term = search?.Trim();
        if (term is { Length: > Ingredient.MaxNameLength })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["search"] = [$"The search text can be at most {Ingredient.MaxNameLength} characters."],
            });
        }

        // Own rows only: the tenant filter admits the global rows (null household) and this household's.
        var query = db.Ingredients.AsNoTracking().WithNames(db, CatalogTranslation.KindIngredient, locale.Code)
            .Where(i => i.Row.HouseholdId != null);
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(i => i.Name.Contains(term));
        }

        var rows = await query.OrderByName(locale.Code).ToListAsync(cancellationToken);
        var usage = await IngredientUsage.LoadAsync(db, [.. rows.Select(r => r.Row.Id)], cancellationToken);
        return Results.Ok(rows.Select(r => new OwnIngredientResponse(
            r.Row.Id, r.Name, r.Row.IsStaple, r.Row.Dimension, usage[r.Row.Id].ToResponse())).ToArray());
    }

    // ---- Rename ----------------------------------------------------------------------------------------------------

    private static async Task<IResult> Rename(
        Guid ingredientId, RenameIngredientRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        var name = Ingredient.NormalizeName(request?.Name);
        if (name is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"The name must be 1 to {Ingredient.MaxNameLength} characters, without control characters."],
            });
        }

        var ingredient = await db.Ingredients.FirstOrDefaultAsync(i => i.Id == ingredientId, cancellationToken);
        if (ingredient is null)
        {
            return NotFound();
        }

        if (ingredient.HouseholdId is null)
        {
            return GlobalReadOnly();
        }

        // Same rules as create: unique among the household's own rows and the global catalog (every language), ignoring case.
        // The row itself is excluded, so changing only the case of its own name is allowed.
        if (await db.Ingredients.AnyAsync(i => i.Id != ingredientId && i.Name == name, cancellationToken)
            || await db.AnyGlobalNameAsync(CatalogTranslation.KindIngredient, name, cancellationToken))
        {
            return Duplicate(name);
        }

        ingredient.Rename(name);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            return Duplicate(name);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Changed();
        }

        return Results.Ok(new IngredientResponse(ingredient.Id, ingredient.Name, ingredient.IsStaple, ingredient.Dimension, false));
    }

    // ---- Delete ----------------------------------------------------------------------------------------------------

    private static async Task<IResult> Delete(Guid ingredientId, AppDbContext db, CancellationToken cancellationToken)
    {
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(i => i.Id == ingredientId, cancellationToken);
        if (ingredient is null)
        {
            return NotFound();
        }

        if (ingredient.HouseholdId is null)
        {
            return GlobalReadOnly();
        }

        var usage = (await IngredientUsage.LoadAsync(db, [ingredientId], cancellationToken))[ingredientId];
        if (usage.Any)
        {
            return InUse(ingredient.Name, usage);
        }

        // An unused ingredient may still be named as a hint in an open import draft; drop those hints in the same save.
        await foreach (var import in OpenDrafts(db).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var draft = ImportDraftJson.Deserialize(import.DraftJson!);
            if (draft.References(ingredientId))
            {
                import.UpdateDraft(ImportDraftJson.Serialize(draft.ReplaceIngredient(ingredientId, null)));
            }
        }

        db.Ingredients.Remove(ingredient);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return await db.Ingredients.AnyAsync(i => i.Id == ingredientId, cancellationToken) ? Changed() : Results.NoContent();
        }
        catch (DbUpdateException)
        {
            // Used between the check and the delete: a NO ACTION foreign key refused it.
            db.ChangeTracker.Clear();
            var after = (await IngredientUsage.LoadAsync(db, [ingredientId], cancellationToken))[ingredientId];
            return after.Any ? InUse(ingredient.Name, after) : throw new InvalidOperationException("The ingredient could not be deleted.");
        }

        return Results.NoContent();
    }

    // ---- Merge -----------------------------------------------------------------------------------------------------

    private static async Task<IResult> Merge(
        Guid householdId, Guid ingredientId, MergeIngredientRequest? request, RequestLocale locale, TimeProvider time, AppDbContext db, IShoppingListNotifier notifier,
        ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        if (request?.TargetIngredientId is not { } targetId || targetId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetIngredientId"] = ["Choose the ingredient to merge into."] });
        }

        var source = await db.Ingredients.FirstOrDefaultAsync(i => i.Id == ingredientId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        if (source.HouseholdId is null)
        {
            return GlobalReadOnly();
        }

        if (targetId == ingredientId)
        {
            return AppProblems.Problem(
                StatusCodes.Status400BadRequest, AppProblems.IngredientMergeIntoSelf, "Cannot merge into itself",
                "Choose a different ingredient to merge into.");
        }

        // Q-012: the id from the body is resolved through the filtered set. Another household's row and an unknown id are
        // the same answer, so ids do not leak.
        var target = await db.Ingredients.AsNoTracking().FirstOrDefaultAsync(i => i.Id == targetId, cancellationToken);
        if (target is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetIngredientId"] = ["The ingredient to merge into does not exist."] });
        }

        MergeOutcome outcome;
        try
        {
            outcome = await IngredientMerger.MergeAsync(db, source, target, time.GetUtcNow(), cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A recipe edit, a plan change or another merge committed in between (stale row, or a new reference to the source
            // that the foreign key refused). The transaction is rolled back, nothing changed.
            db.ChangeTracker.Clear();
            return Changed();
        }

        // After the commit; a failing broadcast is logged and never undoes the change (T-034).
        foreach (var week in outcome.RegeneratedWeeks)
        {
            await ShoppingListEndpoints.NotifyAsync(
                notifier, log, new ShoppingListChange(householdId, week, ShoppingListChangeKind.Generated, null), cancellationToken);
        }

        var warnings = new List<string>();
        if (source.Dimension != target.Dimension)
        {
            warnings.Add("dimension-differs");
        }

        if (source.IsStaple != target.IsStaple)
        {
            warnings.Add("staple-differs");
        }

        var name = await db.Ingredients.AsNoTracking().WithNames(db, CatalogTranslation.KindIngredient, locale.Code)
            .Where(i => i.Row.Id == target.Id).Select(i => i.Name).FirstAsync(cancellationToken);
        return Results.Ok(new MergeIngredientResponse(
            new IngredientResponse(target.Id, name, target.IsStaple, target.Dimension, target.HouseholdId is null),
            outcome.Moved.ToResponse(),
            [.. warnings]));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    internal static IQueryable<Foodplanner.Domain.Imports.RecipeImport> OpenDrafts(AppDbContext db) =>
        db.RecipeImports.Where(i => i.Status == ImportStatus.Draft && i.DraftJson != null);

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.IngredientNotFound, "Ingredient not found");

    private static IResult GlobalReadOnly() =>
        AppProblems.Problem(
            StatusCodes.Status403Forbidden, AppProblems.IngredientGlobal, "Shared ingredient",
            "Shared catalog ingredients cannot be changed, deleted or merged; only the household's own ingredients can.");

    private static IResult Changed() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict, AppProblems.IngredientConflict, "Ingredient changed",
            "Something changed at the same time (a recipe, the plan or another edit). Nothing was changed; reload and try again.");

    private static IResult Duplicate(string name) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.IngredientExists,
            "Ingredient already exists",
            $"There is already an ingredient called '{name}' in this household or in the shared catalog.");

    private static IResult InUse(string name, IngredientUsage usage) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.IngredientInUse,
            "Ingredient in use",
            $"'{name}' is used by {usage.Recipes} recipe(s), {usage.PantryItems} pantry item(s), {usage.ShoppingLists} shopping list(s) and {usage.ImportDrafts} import draft(s). Merge it into another ingredient instead.",
            new Dictionary<string, object?> { ["usage"] = usage.ToResponse() });
}
