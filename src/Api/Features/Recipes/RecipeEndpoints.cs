using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Tags;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Images;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Foodplanner.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Recipes;

public sealed record RecipeStepRequest(string? Text);

/// <summary>
/// An ingredient line: ids and a quantity, never free text. <c>AmountKind</c> is <c>Measured</c> (quantity and unit),
/// <c>ToTaste</c> or <c>AsNeeded</c> (neither); left out it is inferred (a quantity means Measured, none means ToTaste).
/// <c>Purpose</c> is Main (default), Frying, Serving, Garnish or Seasoning. <c>AlternativeIds</c> are other ingredients that
/// can stand in for this one (at most 5, global or the household's own).
/// </summary>
public sealed record RecipeIngredientRequest(
    Guid? IngredientId,
    decimal? Quantity,
    Guid? UnitId,
    string? Note,
    string? AmountKind = null,
    string? Purpose = null,
    bool? IsOptional = null,
    Guid[]? AlternativeIds = null);

public sealed record RecipeRequest(
    string? Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    RecipeStepRequest[]? Steps,
    RecipeIngredientRequest[]? Ingredients,
    Guid[]? TagIds,
    string? SourceUrl = null,
    string? SourceName = null);

/// <summary>A grid card: enough to list recipes. <c>TotalMinutes</c> is prep plus cook, null when neither is set.</summary>
public sealed record RecipeSummaryResponse(Guid Id, string Title, int Servings, int? TotalMinutes, TagResponse[] Tags, string? ThumbUrl, string? CardUrl);

public sealed record RecipeListResponse(RecipeSummaryResponse[] Items, int Total, int Limit, int Offset);

public sealed record RecipeStepResponse(int Position, string Text);

/// <summary>Names are in the request language (Accept-Language); <c>UnitCode</c> is the stable code, <c>UnitName</c> its name.</summary>
public sealed record RecipeIngredientResponse(
    Guid IngredientId,
    string IngredientName,
    decimal? Quantity,
    Guid? UnitId,
    string? UnitCode,
    string? UnitName,
    string? Note,
    AmountKind AmountKind,
    IngredientPurpose Purpose,
    bool IsOptional,
    RecipeAlternativeResponse[] Alternatives);

/// <summary>An ingredient that can stand in for the line's own, named in the request language.</summary>
public sealed record RecipeAlternativeResponse(Guid IngredientId, string IngredientName);

public sealed record RecipeResponse(
    Guid Id,
    string Title,
    string? Description,
    int Servings,
    int? PrepMinutes,
    int? CookMinutes,
    int? TotalMinutes,
    RecipeStepResponse[] Steps,
    RecipeIngredientResponse[] Ingredients,
    TagResponse[] Tags,
    RecipeImageUrlsResponse? Image,
    string? SourceUrl,
    string? SourceName);

/// <summary>Ingredient lines scaled to <c>Servings</c> (from <c>BaseServings</c>), quantities and units ready to display.</summary>
public sealed record ScaledRecipeResponse(Guid RecipeId, int BaseServings, int Servings, RecipeIngredientResponse[] Ingredients);

/// <summary>Recipe CRUD (T-021). Any member reads; only an Adult or Owner writes.</summary>
public static class RecipeEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxSearchLength = 100;
    public const int MaxTagsPerRecipe = 20;
    public const int MaxFilterIds = 10;

    public static IEndpointRouteBuilder MapRecipeEndpoints(this IEndpointRouteBuilder api)
    {
        var recipes = api.MapHouseholdScopedGroup().MapGroup("/recipes");

        recipes.MapGet("", ListRecipes)
            .WithName("ListRecipes")
            .Produces<RecipeListResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        recipes.MapGet("/{recipeId:guid}", GetRecipe)
            .WithName("GetRecipe")
            .Produces<RecipeResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        recipes.MapGet("/{recipeId:guid}/scaled", GetScaledRecipe)
            .WithName("GetScaledRecipe")
            .Produces<ScaledRecipeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var write = recipes.MapGroup("").RequireAuthorization(Policies.HouseholdAdult);
        write.MapPost("", CreateRecipe)
            .WithName("CreateRecipe")
            .Produces<RecipeResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        write.MapPut("/{recipeId:guid}", UpdateRecipe)
            .WithName("UpdateRecipe")
            .Produces<RecipeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        write.MapDelete("/{recipeId:guid}", DeleteRecipe)
            .WithName("DeleteRecipe")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>
    /// Recipes of the route's household (the query filter), sorted by title then id (a total order, so offset paging
    /// has no duplicates or gaps, and inserting a later title never shifts an earlier page). All filters combine with AND:
    /// <c>search</c> (case-insensitive "contains" on the title), <c>tagIds</c> (the recipe has every one of them),
    /// <c>maxMinutes</c> (prep plus cook at most n; a recipe with neither time set is not matched, because its time is
    /// unknown) and <c>ingredientId</c> (the recipe has a line for every one of them). An unknown tag or ingredient id
    /// simply matches nothing.
    /// </summary>
    private static async Task<IResult> ListRecipes(
        Guid householdId, string? search, Guid[]? tagIds, int? maxMinutes, Guid[]? ingredientId, int? limit, int? offset,
        RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var term = search?.Trim();
        if (term is { Length: > MaxSearchLength })
        {
            errors["search"] = [$"The search text can be at most {MaxSearchLength} characters."];
        }

        if (limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"The limit must be 1 to {MaxLimit}."];
        }

        if (offset is < 0)
        {
            errors["offset"] = ["The offset cannot be negative."];
        }

        if (maxMinutes is < 0 or > Recipe.MaxMinutes * 2)
        {
            errors["maxMinutes"] = [$"The maximum minutes must be 0 to {Recipe.MaxMinutes * 2}."];
        }

        var tags = (tagIds ?? []).Distinct().ToList();
        if (tags.Count > MaxFilterIds || tags.Contains(Guid.Empty))
        {
            errors["tagIds"] = [$"Give at most {MaxFilterIds} valid tag ids."];
        }

        var ingredients = (ingredientId ?? []).Distinct().ToList();
        if (ingredients.Count > MaxFilterIds || ingredients.Contains(Guid.Empty))
        {
            errors["ingredientId"] = [$"Give at most {MaxFilterIds} valid ingredient ids."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Recipes.AsNoTracking();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(r => r.Title.Contains(term));
        }

        if (maxMinutes is { } max)
        {
            query = query.Where(r => (r.PrepMinutes != null || r.CookMinutes != null) && (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0) <= max);
        }

        foreach (var tag in tags)
        {
            var id = tag;
            query = query.Where(r => db.RecipeTags.Any(t => t.RecipeId == r.Id && t.TagId == id));
        }

        foreach (var ingredient in ingredients)
        {
            var id = ingredient;
            query = query.Where(r => db.RecipeIngredients.Any(l => l.RecipeId == r.Id && l.IngredientId == id));
        }

        var take = limit ?? DefaultLimit;
        var skip = offset ?? 0;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(r => r.Title).ThenBy(r => r.Id).Skip(skip).Take(take)
            .Select(r => new { r.Id, r.Title, r.Servings, r.PrepMinutes, r.CookMinutes, r.ImageVersion })
            .ToListAsync(cancellationToken);

        var tagsByRecipe = await LoadTagsAsync(db, rows.Select(r => r.Id).ToList(), locale.Code, cancellationToken);
        var items = rows.Select(r => new RecipeSummaryResponse(
            r.Id, r.Title, r.Servings,
            r.PrepMinutes is null && r.CookMinutes is null ? null : (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0),
            tagsByRecipe.GetValueOrDefault(r.Id, []),
            RecipeImageUrls.For(householdId, r.Id, r.ImageVersion, RecipeImageSize.Thumb),
            RecipeImageUrls.For(householdId, r.Id, r.ImageVersion, RecipeImageSize.Card))).ToArray();
        return Results.Ok(new RecipeListResponse(items, total, take, skip));
    }

    /// <summary>The tags of the given recipes, categories first, then by name.</summary>
    private static async Task<Dictionary<Guid, TagResponse[]>> LoadTagsAsync(
        AppDbContext db, List<Guid> recipeIds, string locale, CancellationToken cancellationToken)
    {
        if (recipeIds.Count == 0)
        {
            return [];
        }

        var links = await db.RecipeTags.AsNoTracking().Where(l => recipeIds.Contains(l.RecipeId))
            .Select(l => new { l.RecipeId, l.TagId }).ToListAsync(cancellationToken);
        var tagIds = links.Select(l => l.TagId).Distinct().ToList();
        var tags = await db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id)).WithNames(db, CatalogTranslation.KindTag, locale)
            .OrderBy(t => t.Row.Kind).ThenByName(locale).ToListAsync(cancellationToken);
        var order = tags.Select((t, index) => (t.Row.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var byId = tags.ToDictionary(t => t.Row.Id);
        return links.GroupBy(l => l.RecipeId).ToDictionary(
            g => g.Key,
            g => g.Where(l => byId.ContainsKey(l.TagId)).OrderBy(l => order[l.TagId]).Select(l => TagEndpoints.ToResponse(byId[l.TagId])).ToArray());
    }

    private static async Task<IResult> GetRecipe(Guid recipeId, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        return recipe is null ? NotFound() : Results.Ok(await LoadAsync(db, recipe, locale.Code, cancellationToken));
    }

    /// <summary>
    /// A recipe's ingredient lines for another number of servings: the quantities scaled by the Domain
    /// (<see cref="RecipeScaling"/>) and made readable (<see cref="QuantityMerger.ForDisplay"/>: rounding steps and display
    /// unit, so 1500 g reads 1,5 kg). To taste stays unchanged. <c>Quantity</c>/<c>Unit*</c> are the display values.
    /// <c>servings</c> is 1 to 100 and defaults to the recipe's own servings.
    /// </summary>
    private static async Task<IResult> GetScaledRecipe(
        Guid recipeId, int? servings, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        if (servings is < RecipeScaling.MinServings or > RecipeScaling.MaxServings)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["servings"] = [$"The servings must be {RecipeScaling.MinServings} to {RecipeScaling.MaxServings}."],
            });
        }

        var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return NotFound();
        }

        var target = servings ?? recipe.Servings;
        var loaded = await LoadAsync(db, recipe, locale.Code, cancellationToken);
        var units = await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale.Code).ToListAsync(cancellationToken);
        var catalog = new UnitCatalog(units.Select(u => u.Row));
        var byCode = units.ToDictionary(u => u.Row.Code, StringComparer.Ordinal);
        var byId = units.ToDictionary(u => u.Row.Id);

        var lines = loaded.Ingredients.Select(line =>
        {
            // To taste and as needed are not amounts: they are returned as they are for any number of servings.
            if (line.AmountKind != AmountKind.Measured || line.Quantity is null || line.UnitId is not { } unitId || !byId.TryGetValue(unitId, out var unit))
            {
                return line;
            }

            var scaled = RecipeScaling.ScaleForDisplay(new Quantity(line.Quantity, unit.Row), recipe.Servings, target, catalog);
            var shown = byCode[scaled.Unit.Code];
            return line with { Quantity = scaled.Amount, UnitId = shown.Row.Id, UnitCode = shown.Row.Code, UnitName = shown.Name };
        }).ToArray();

        return Results.Ok(new ScaledRecipeResponse(recipe.Id, recipe.Servings, target, lines));
    }

    private static async Task<IResult> CreateRecipe(
        Guid householdId, RecipeRequest? request, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var recipe = await TryCreateAsync(db, request, errors, cancellationToken);
        if (recipe is null)
        {
            return Results.ValidationProblem(errors);
        }

        return Results.Created(
            $"/api/households/{householdId}/recipes/{recipe.Id}", await LoadAsync(db, recipe, locale.Code, cancellationToken));
    }

    /// <summary>
    /// The create path of a recipe: the same validation, the same resolution of every id through the filtered sets (Q-012)
    /// and the same writes for the endpoint and for saving a recipe import (T-042). Returns null with <paramref name="errors"/>
    /// filled when the request is invalid. It joins a transaction the caller has already opened, otherwise it opens its own;
    /// either way nothing is written unless the whole recipe is.
    /// </summary>
    internal static async Task<Recipe?> TryCreateAsync(
        AppDbContext db, RecipeRequest? request, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var valid = Validate(request, errors);
        if (valid is null || errors.Count > 0 || await ResolveReferencesAsync(db, request!, errors, cancellationToken) is false)
        {
            return null;
        }

        var recipe = new Recipe(valid.Title, valid.Description, valid.Servings, valid.PrepMinutes, valid.CookMinutes);
        recipe.SetSource(valid.SourceUrl, valid.SourceName);
        // Saves in one transaction: the children need the generated recipe id, the alternatives the generated line ids, and a
        // failed later save must not leave a bare recipe.
        var ownTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(cancellationToken);
        var lines = AddChildren(db, recipe.Id, request!);
        foreach (var tagId in DistinctTagIds(request!))
        {
            db.RecipeTags.Add(new RecipeTag(recipe.Id, tagId));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (AddAlternatives(db, lines, request!))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return recipe;
    }

    /// <summary>Replaces the recipe's fields, steps and ingredients (tracked deletes and inserts, in one save).</summary>
    private static async Task<IResult> UpdateRecipe(
        Guid recipeId, RecipeRequest? request, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var valid = Validate(request, errors);
        if (valid is null || errors.Count > 0 || await ResolveReferencesAsync(db, request!, errors, cancellationToken) is false)
        {
            return Results.ValidationProblem(errors);
        }

        var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return NotFound();
        }

        recipe.Update(valid.Title, valid.Description, valid.Servings, valid.PrepMinutes, valid.CookMinutes);
        recipe.SetSource(valid.SourceUrl, valid.SourceName);
        db.RecipeSteps.RemoveRange(await db.RecipeSteps.Where(s => s.RecipeId == recipeId).ToListAsync(cancellationToken));
        // The database removes the old lines' alternatives with them (cascade).
        db.RecipeIngredients.RemoveRange(await db.RecipeIngredients.Where(i => i.RecipeId == recipeId).ToListAsync(cancellationToken));
        var lines = AddChildren(db, recipeId, request!);

        // Tags: tracked diff (remove the ones no longer wanted, add the new ones), so an unchanged tag is never deleted and re-inserted.
        var wanted = DistinctTagIds(request!).ToHashSet();
        var current = await db.RecipeTags.Where(t => t.RecipeId == recipeId).ToListAsync(cancellationToken);
        db.RecipeTags.RemoveRange(current.Where(t => !wanted.Contains(t.TagId)));
        foreach (var tagId in wanted.Except(current.Select(t => t.TagId)))
        {
            db.RecipeTags.Add(new RecipeTag(recipeId, tagId));
        }

        // Two saves in one transaction: the alternatives need the generated ids of the new lines.
        var ownTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (AddAlternatives(db, lines, request!) && await TrySaveAsync(db, cancellationToken) is { } conflictAgain)
        {
            return conflictAgain;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await LoadAsync(db, recipe, locale.Code, cancellationToken));
    }

    private static async Task<IResult> DeleteRecipe(
        Guid householdId, Guid recipeId, AppDbContext db, IImageStore images, ILogger<Recipe> logger, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return NotFound();
        }

        // A recipe on a meal plan is in use (T-030): the planned meals would lose their recipe, so it is refused (409).
        if (await db.MealPlanEntries.AnyAsync(e => e.RecipeId == recipeId, cancellationToken))
        {
            return RecipeInUse();
        }

        // A shopping list may still name the recipe as a source of an item (T-033); the list is stale until it is
        // regenerated, so the source rows go with the recipe.
        var sources = await db.ShoppingListItemSources.Where(s => s.RecipeId == recipeId).ToListAsync(cancellationToken);
        db.ShoppingListItemSources.RemoveRange(sources);

        // Steps and ingredient lines go with the recipe (database cascade).
        var hadImage = recipe.ImageVersion is not null;
        db.Recipes.Remove(recipe);
        try
        {
            if (await TrySaveAsync(db, cancellationToken) is { } conflict)
            {
                return conflict;
            }
        }
        catch (DbUpdateException)
        {
            // Planned between the check and the delete: the NO ACTION foreign key refused it.
            db.ChangeTracker.Clear();
            if (await db.MealPlanEntries.AnyAsync(e => e.RecipeId == recipeId, cancellationToken))
            {
                return RecipeInUse();
            }

            throw;
        }

        if (hadImage)
        {
            await RecipeImageEndpoints.TryDeleteBlobsAsync(images, logger, RecipeImageBlobNames.RecipePrefix(householdId, recipeId), cancellationToken);
        }

        return Results.NoContent();
    }

    private static IResult RecipeInUse() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.RecipeInUse,
            "Recipe is planned",
            "The recipe is on a meal plan. Remove it from the plan first.");

    /// <summary>
    /// Saves; a <see cref="DbUpdateConcurrencyException"/> means the row is gone or no longer belongs to this household
    /// (a concurrent delete, or a forged detached entity, which T-008 turns into this exception) and is answered 409,
    /// not a 500. Null when saved.
    /// </summary>
    public static async Task<IResult?> TrySaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException
            // An ingredient or tag referenced by a line was deleted or merged away between the check and the save (T-079): foreign key violation.
            || (exception is DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException { Number: 547 } }))
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.RecipeConflict,
                "Recipe changed",
                "The recipe was changed or deleted by someone else. Reload it and try again.");
        }
    }

    /// <summary>Adds the steps and the ingredient lines; returns the lines in request order (the alternatives need their ids).</summary>
    private static List<RecipeIngredient> AddChildren(AppDbContext db, Guid recipeId, RecipeRequest request)
    {
        var steps = request.Steps ?? [];
        for (var i = 0; i < steps.Length; i++)
        {
            db.RecipeSteps.Add(new RecipeStep(recipeId, i, steps[i].Text!.Trim()));
        }

        var requested = request.Ingredients ?? [];
        var added = new List<RecipeIngredient>(requested.Length);
        for (var i = 0; i < requested.Length; i++)
        {
            var line = requested[i];
            var (kind, purpose) = ParseKindAndPurpose(line);
            var entity = new RecipeIngredient(
                recipeId, i, line.IngredientId!.Value, line.Quantity, line.UnitId, NormalizeNote(line.Note),
                kind, purpose, line.IsOptional ?? false);
            db.RecipeIngredients.Add(entity);
            added.Add(entity);
        }

        return added;
    }

    /// <summary>Adds the alternatives of already saved lines (repeats ignored). True when there is anything to save.</summary>
    private static bool AddAlternatives(AppDbContext db, List<RecipeIngredient> lines, RecipeRequest request)
    {
        var requested = request.Ingredients ?? [];
        var any = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var position = 0;
            foreach (var id in DistinctAlternativeIds(requested[i]))
            {
                db.RecipeIngredientAlternatives.Add(new RecipeIngredientAlternative(lines[i].Id, position++, id));
                any = true;
            }
        }

        return any;
    }

    private static List<Guid> DistinctAlternativeIds(RecipeIngredientRequest line) => (line.AlternativeIds ?? []).Distinct().ToList();

    /// <summary>The kind and purpose of a validated line; a kind left out is inferred from the quantity.</summary>
    private static (AmountKind Kind, IngredientPurpose Purpose) ParseKindAndPurpose(RecipeIngredientRequest line) =>
        (AmountKinds.TryParse(line.AmountKind, out var kind) ? kind : AmountRules.Infer(line.Quantity),
         IngredientPurposes.TryParse(line.Purpose, out var purpose) ? purpose : IngredientPurpose.Main);

    private static List<Guid> DistinctTagIds(RecipeRequest request) => (request.TagIds ?? []).Distinct().ToList();

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private sealed record ValidRecipe(
        string Title, string? Description, int Servings, int? PrepMinutes, int? CookMinutes, string? SourceUrl, string? SourceName);

    /// <summary>Shape and range checks only; whether the ids exist for this household is <see cref="ResolveReferencesAsync"/>.</summary>
    private static ValidRecipe? Validate(RecipeRequest? request, Dictionary<string, string[]> errors)
    {
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
            return null;
        }

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > Recipe.MaxTitleLength || title.Any(char.IsControl))
        {
            errors["title"] = [$"The title must be 1 to {Recipe.MaxTitleLength} characters, without control characters."];
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > Recipe.MaxDescriptionLength })
        {
            errors["description"] = [$"The description can be at most {Recipe.MaxDescriptionLength} characters."];
        }

        if (request.Servings is null or < Recipe.MinServings or > Recipe.MaxServings)
        {
            errors["servings"] = [$"Servings must be {Recipe.MinServings} to {Recipe.MaxServings}."];
        }

        var sourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl) ? null : request.SourceUrl.Trim();
        if (sourceUrl is not null && !Recipe.IsValidSourceUrl(sourceUrl))
        {
            errors["sourceUrl"] = [$"The source must be an absolute http or https link of at most {Recipe.MaxSourceUrlLength} characters."];
        }

        var sourceName = string.IsNullOrWhiteSpace(request.SourceName) ? null : request.SourceName.Trim();
        if (sourceName is { Length: > Recipe.MaxSourceNameLength } || sourceName?.Any(char.IsControl) == true)
        {
            errors["sourceName"] = [$"The source name can be at most {Recipe.MaxSourceNameLength} characters, without control characters."];
        }

        CheckMinutes(request.PrepMinutes, "prepMinutes", errors);
        CheckMinutes(request.CookMinutes, "cookMinutes", errors);

        var steps = request.Steps ?? [];
        if (steps.Length > Recipe.MaxSteps)
        {
            errors["steps"] = [$"A recipe can have at most {Recipe.MaxSteps} steps."];
        }
        else
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var text = steps[i]?.Text?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length > RecipeStep.MaxTextLength)
                {
                    errors[$"steps[{i}].text"] = [$"A step must be 1 to {RecipeStep.MaxTextLength} characters."];
                }
            }
        }

        var lines = request.Ingredients ?? [];
        if (lines.Length > Recipe.MaxIngredients)
        {
            errors["ingredients"] = [$"A recipe can have at most {Recipe.MaxIngredients} ingredients."];
        }
        else
        {
            for (var i = 0; i < lines.Length; i++)
            {
                CheckLine(lines[i], i, errors);
            }
        }

        var requestedTags = request.TagIds ?? [];
        if (requestedTags.Length > MaxTagsPerRecipe)
        {
            errors["tagIds"] = [$"A recipe can have at most {MaxTagsPerRecipe} tags."];
        }
        else
        {
            for (var i = 0; i < requestedTags.Length; i++)
            {
                if (requestedTags[i] == Guid.Empty)
                {
                    errors[$"tagIds[{i}]"] = ["A tag id is required."];
                }
            }
        }

        return errors.Count > 0 ? null : new ValidRecipe(
            title!, description, request.Servings!.Value, request.PrepMinutes, request.CookMinutes, sourceUrl, sourceName);
    }

    private static void CheckMinutes(int? minutes, string key, Dictionary<string, string[]> errors)
    {
        if (minutes is < 0 or > Recipe.MaxMinutes)
        {
            errors[key] = [$"Minutes must be 0 to {Recipe.MaxMinutes}."];
        }
    }

    private static void CheckLine(RecipeIngredientRequest? line, int index, Dictionary<string, string[]> errors)
    {
        var key = $"ingredients[{index}]";
        if (line is null)
        {
            errors[key] = ["The ingredient line is required."];
            return;
        }

        if (line.IngredientId is null || line.IngredientId == Guid.Empty)
        {
            errors[$"{key}.ingredientId"] = ["An ingredient is required."];
        }

        var kind = AmountRules.Infer(line.Quantity);
        if (!string.IsNullOrWhiteSpace(line.AmountKind))
        {
            if (AmountKinds.TryParse(line.AmountKind.Trim(), out var parsed))
            {
                kind = parsed;
            }
            else
            {
                errors[$"{key}.amountKind"] = ["The amount kind must be Measured, ToTaste or AsNeeded."];
            }
        }

        if (!string.IsNullOrWhiteSpace(line.Purpose) && !IngredientPurposes.TryParse(line.Purpose.Trim(), out _))
        {
            errors[$"{key}.purpose"] = ["The purpose must be Main, Frying, Serving, Garnish or Seasoning."];
        }

        if (line.Quantity is { } quantity
            && (quantity <= 0 || quantity > RecipeIngredient.MaxQuantity || decimal.Round(quantity, RecipeIngredient.MaxQuantityDecimals) != quantity))
        {
            errors[$"{key}.quantity"] =
                [$"The quantity must be greater than 0 and at most {RecipeIngredient.MaxQuantity:0}, with at most {RecipeIngredient.MaxQuantityDecimals} decimals."];
        }

        if (!errors.ContainsKey($"{key}.amountKind"))
        {
            switch (AmountRules.Check(kind, line.Quantity, line.UnitId))
            {
                case AmountProblem.QuantityRequired:
                    errors.TryAdd($"{key}.quantity", ["A measured amount needs a quantity."]);
                    break;
                case AmountProblem.UnitRequired:
                    errors[$"{key}.unitId"] = ["A unit is required when there is a quantity."];
                    break;
                case AmountProblem.QuantityForbidden:
                    errors.TryAdd($"{key}.quantity", ["Leave the quantity out when the amount is to taste or as needed."]);
                    break;
                case AmountProblem.UnitForbidden:
                    errors[$"{key}.unitId"] = ["Leave the unit out when there is no quantity (to taste or as needed)."];
                    break;
            }
        }

        if (line.Note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            errors[$"{key}.note"] = [$"The note can be at most {RecipeIngredient.MaxNoteLength} characters."];
        }

        var alternatives = line.AlternativeIds ?? [];
        if (alternatives.Length > RecipeIngredientAlternative.MaxPerLine)
        {
            errors[$"{key}.alternativeIds"] = [$"A line can have at most {RecipeIngredientAlternative.MaxPerLine} alternatives."];
        }
        else
        {
            for (var j = 0; j < alternatives.Length; j++)
            {
                if (alternatives[j] == Guid.Empty)
                {
                    errors[$"{key}.alternativeIds[{j}]"] = ["An alternative ingredient id is required."];
                }
                else if (alternatives[j] == line.IngredientId)
                {
                    errors[$"{key}.alternativeIds[{j}]"] = ["An alternative must be a different ingredient."];
                }
            }
        }
    }

    /// <summary>
    /// Q-012 rule: every client-supplied id is resolved through the filtered sets before anything is saved. An ingredient
    /// of another household (or any unknown id) is not found, and the answer is 400 ValidationProblem on that line, so the
    /// response is the same for "does not exist" and "belongs to someone else" and nothing is written. Units are global.
    /// Returns false when a reference is not found (errors filled).
    /// </summary>
    private static async Task<bool> ResolveReferencesAsync(
        AppDbContext db, RecipeRequest request, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var lines = request.Ingredients ?? [];
        var ingredientIds = lines.Select(l => l.IngredientId!.Value).Concat(lines.SelectMany(l => l.AlternativeIds ?? [])).Distinct().ToList();
        var unitIds = lines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value).Distinct().ToList();

        var knownIngredients = ingredientIds.Count == 0
            ? []
            : (await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
        var knownUnits = unitIds.Count == 0
            ? []
            : (await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken)).ToHashSet();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!knownIngredients.Contains(lines[i].IngredientId!.Value))
            {
                errors[$"ingredients[{i}].ingredientId"] = ["The ingredient does not exist."];
            }

            if (lines[i].UnitId is { } unitId && !knownUnits.Contains(unitId))
            {
                errors[$"ingredients[{i}].unitId"] = ["The unit does not exist."];
            }

            // An alternative is resolved like the line's own ingredient: another household's id is "does not exist" (T-073 is the backstop).
            var alternatives = lines[i].AlternativeIds ?? [];
            for (var j = 0; j < alternatives.Length; j++)
            {
                if (!knownIngredients.Contains(alternatives[j]))
                {
                    errors[$"ingredients[{i}].alternativeIds[{j}]"] = ["The ingredient does not exist."];
                }
            }
        }

        // Tags: global or the household's own, through the filtered set; another household's tag is "not found".
        var requested = DistinctTagIds(request);
        if (requested.Count > 0)
        {
            var knownTags = (await db.Tags.AsNoTracking().Where(t => requested.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
            var all = request.TagIds ?? [];
            for (var i = 0; i < all.Length; i++)
            {
                if (!knownTags.Contains(all[i]))
                {
                    errors[$"tagIds[{i}]"] = ["The tag does not exist."];
                }
            }
        }

        return errors.Count == 0;
    }

    private static async Task<RecipeResponse> LoadAsync(AppDbContext db, Recipe recipe, string locale, CancellationToken cancellationToken)
    {
        var steps = await db.RecipeSteps.AsNoTracking().Where(s => s.RecipeId == recipe.Id)
            .OrderBy(s => s.Position).Select(s => new RecipeStepResponse(s.Position, s.Text)).ToListAsync(cancellationToken);
        var lines = await db.RecipeIngredients.AsNoTracking().Where(i => i.RecipeId == recipe.Id)
            .OrderBy(i => i.Position).ToListAsync(cancellationToken);

        var lineIds = lines.Select(l => l.Id).ToList();
        var alternatives = (await db.RecipeIngredientAlternatives.AsNoTracking().Where(a => lineIds.Contains(a.RecipeIngredientId))
            .OrderBy(a => a.Position).Select(a => new { a.RecipeIngredientId, a.IngredientId }).ToListAsync(cancellationToken))
            .ToLookup(a => a.RecipeIngredientId, a => a.IngredientId);
        var ingredientIds = lines.Select(l => l.IngredientId).Concat(alternatives.SelectMany(a => a)).Distinct().ToList();
        var unitIds = lines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value).Distinct().ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        var units = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).WithNames(db, CatalogTranslation.KindUnit, locale)
            .ToDictionaryAsync(u => u.Row.Id, u => (u.Row.Code, u.Name), cancellationToken);

        var ingredients = lines.Select(l => new RecipeIngredientResponse(
            l.IngredientId,
            names.GetValueOrDefault(l.IngredientId, string.Empty),
            l.Quantity,
            l.UnitId,
            l.UnitId is { } unitId ? units.GetValueOrDefault(unitId).Code : null,
            l.UnitId is { } unitId2 ? units.GetValueOrDefault(unitId2).Name : null,
            l.Note,
            l.AmountKind,
            l.Purpose,
            l.IsOptional,
            [.. alternatives[l.Id].Select(id => new RecipeAlternativeResponse(id, names.GetValueOrDefault(id, string.Empty)))])).ToArray();

        return new RecipeResponse(
            recipe.Id, recipe.Title, recipe.Description, recipe.Servings, recipe.PrepMinutes, recipe.CookMinutes,
            recipe.TotalMinutes, [.. steps], ingredients,
            (await LoadTagsAsync(db, [recipe.Id], locale, cancellationToken)).GetValueOrDefault(recipe.Id, []),
            RecipeImageUrls.All(recipe),
            recipe.SourceUrl,
            recipe.SourceDisplayName);
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Recipe not found");
}
