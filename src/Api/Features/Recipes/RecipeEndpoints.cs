using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Tags;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Recipes;

public sealed record RecipeStepRequest(string? Text);

/// <summary>An ingredient line: ids and a quantity, never free text. No quantity and no unit means "to taste".</summary>
public sealed record RecipeIngredientRequest(Guid? IngredientId, decimal? Quantity, Guid? UnitId, string? Note);

public sealed record RecipeRequest(
    string? Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    RecipeStepRequest[]? Steps,
    RecipeIngredientRequest[]? Ingredients,
    Guid[]? TagIds);

/// <summary>A grid card: enough to list recipes. <c>TotalMinutes</c> is prep plus cook, null when neither is set.</summary>
public sealed record RecipeSummaryResponse(Guid Id, string Title, int Servings, int? TotalMinutes, TagResponse[] Tags);

public sealed record RecipeListResponse(RecipeSummaryResponse[] Items, int Total, int Limit, int Offset);

public sealed record RecipeStepResponse(int Position, string Text);

public sealed record RecipeIngredientResponse(
    Guid IngredientId, string IngredientName, decimal? Quantity, Guid? UnitId, string? UnitCode, string? Note);

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
    TagResponse[] Tags);

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
        string? search, Guid[]? tagIds, int? maxMinutes, Guid[]? ingredientId, int? limit, int? offset,
        AppDbContext db, CancellationToken cancellationToken)
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
            .Select(r => new { r.Id, r.Title, r.Servings, r.PrepMinutes, r.CookMinutes })
            .ToListAsync(cancellationToken);

        var tagsByRecipe = await LoadTagsAsync(db, rows.Select(r => r.Id).ToList(), cancellationToken);
        var items = rows.Select(r => new RecipeSummaryResponse(
            r.Id, r.Title, r.Servings,
            r.PrepMinutes is null && r.CookMinutes is null ? null : (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0),
            tagsByRecipe.GetValueOrDefault(r.Id, []))).ToArray();
        return Results.Ok(new RecipeListResponse(items, total, take, skip));
    }

    /// <summary>The tags of the given recipes, categories first, then by name.</summary>
    private static async Task<Dictionary<Guid, TagResponse[]>> LoadTagsAsync(
        AppDbContext db, List<Guid> recipeIds, CancellationToken cancellationToken)
    {
        if (recipeIds.Count == 0)
        {
            return [];
        }

        var rows = await (from link in db.RecipeTags.AsNoTracking()
                          join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                          where recipeIds.Contains(link.RecipeId)
                          orderby tag.Kind, tag.Name, tag.Id
                          select new { link.RecipeId, Tag = tag }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.RecipeId).ToDictionary(g => g.Key, g => g.Select(r => TagEndpoints.ToResponse(r.Tag)).ToArray());
    }

    private static async Task<IResult> GetRecipe(Guid recipeId, AppDbContext db, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        return recipe is null ? NotFound() : Results.Ok(await LoadAsync(db, recipe, cancellationToken));
    }

    private static async Task<IResult> CreateRecipe(
        Guid householdId, RecipeRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var valid = Validate(request, errors);
        if (valid is null || errors.Count > 0 || await ResolveReferencesAsync(db, request!, errors, cancellationToken) is false)
        {
            return Results.ValidationProblem(errors);
        }

        var recipe = new Recipe(valid.Title, valid.Description, valid.Servings, valid.PrepMinutes, valid.CookMinutes);
        // Two saves in one transaction: the children need the generated recipe id, and a failed second save must not leave a bare recipe.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(cancellationToken);
        AddChildren(db, recipe.Id, request!);
        foreach (var tagId in DistinctTagIds(request!))
        {
            db.RecipeTags.Add(new RecipeTag(recipe.Id, tagId));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Created(
            $"/api/households/{householdId}/recipes/{recipe.Id}", await LoadAsync(db, recipe, cancellationToken));
    }

    /// <summary>Replaces the recipe's fields, steps and ingredients (tracked deletes and inserts, in one save).</summary>
    private static async Task<IResult> UpdateRecipe(
        Guid recipeId, RecipeRequest? request, AppDbContext db, CancellationToken cancellationToken)
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
        db.RecipeSteps.RemoveRange(await db.RecipeSteps.Where(s => s.RecipeId == recipeId).ToListAsync(cancellationToken));
        db.RecipeIngredients.RemoveRange(await db.RecipeIngredients.Where(i => i.RecipeId == recipeId).ToListAsync(cancellationToken));
        AddChildren(db, recipeId, request!);

        // Tags: tracked diff (remove the ones no longer wanted, add the new ones), so an unchanged tag is never deleted and re-inserted.
        var wanted = DistinctTagIds(request!).ToHashSet();
        var current = await db.RecipeTags.Where(t => t.RecipeId == recipeId).ToListAsync(cancellationToken);
        db.RecipeTags.RemoveRange(current.Where(t => !wanted.Contains(t.TagId)));
        foreach (var tagId in wanted.Except(current.Select(t => t.TagId)))
        {
            db.RecipeTags.Add(new RecipeTag(recipeId, tagId));
        }

        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return Results.Ok(await LoadAsync(db, recipe, cancellationToken));
    }

    private static async Task<IResult> DeleteRecipe(Guid recipeId, AppDbContext db, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return NotFound();
        }

        // Steps and ingredient lines go with the recipe (database cascade).
        db.Recipes.Remove(recipe);
        return await TrySaveAsync(db, cancellationToken) ?? Results.NoContent();
    }

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
        catch (DbUpdateConcurrencyException)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.RecipeConflict,
                "Recipe changed",
                "The recipe was changed or deleted by someone else. Reload it and try again.");
        }
    }

    private static void AddChildren(AppDbContext db, Guid recipeId, RecipeRequest request)
    {
        var steps = request.Steps ?? [];
        for (var i = 0; i < steps.Length; i++)
        {
            db.RecipeSteps.Add(new RecipeStep(recipeId, i, steps[i].Text!.Trim()));
        }

        var lines = request.Ingredients ?? [];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            db.RecipeIngredients.Add(new RecipeIngredient(
                recipeId, i, line.IngredientId!.Value, line.Quantity, line.UnitId, NormalizeNote(line.Note)));
        }
    }

    private static List<Guid> DistinctTagIds(RecipeRequest request) => (request.TagIds ?? []).Distinct().ToList();

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private sealed record ValidRecipe(string Title, string? Description, int Servings, int? PrepMinutes, int? CookMinutes);

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

        return errors.Count > 0 ? null : new ValidRecipe(title!, description, request.Servings!.Value, request.PrepMinutes, request.CookMinutes);
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

        if (line.Quantity is { } quantity)
        {
            if (quantity <= 0 || quantity > RecipeIngredient.MaxQuantity || decimal.Round(quantity, RecipeIngredient.MaxQuantityDecimals) != quantity)
            {
                errors[$"{key}.quantity"] =
                    [$"The quantity must be greater than 0 and at most {RecipeIngredient.MaxQuantity:0}, with at most {RecipeIngredient.MaxQuantityDecimals} decimals."];
            }

            if (line.UnitId is null || line.UnitId == Guid.Empty)
            {
                errors[$"{key}.unitId"] = ["A unit is required when there is a quantity."];
            }
        }
        else if (line.UnitId is not null)
        {
            errors[$"{key}.unitId"] = ["Leave the unit out when there is no quantity (to taste)."];
        }

        if (line.Note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            errors[$"{key}.note"] = [$"The note can be at most {RecipeIngredient.MaxNoteLength} characters."];
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
        var ingredientIds = lines.Select(l => l.IngredientId!.Value).Distinct().ToList();
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

    private static async Task<RecipeResponse> LoadAsync(AppDbContext db, Recipe recipe, CancellationToken cancellationToken)
    {
        var steps = await db.RecipeSteps.AsNoTracking().Where(s => s.RecipeId == recipe.Id)
            .OrderBy(s => s.Position).Select(s => new RecipeStepResponse(s.Position, s.Text)).ToListAsync(cancellationToken);
        var lines = await db.RecipeIngredients.AsNoTracking().Where(i => i.RecipeId == recipe.Id)
            .OrderBy(i => i.Position).ToListAsync(cancellationToken);

        var ingredientIds = lines.Select(l => l.IngredientId).Distinct().ToList();
        var unitIds = lines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value).Distinct().ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);
        var codes = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);

        var ingredients = lines.Select(l => new RecipeIngredientResponse(
            l.IngredientId,
            names.GetValueOrDefault(l.IngredientId, string.Empty),
            l.Quantity,
            l.UnitId,
            l.UnitId is { } unitId ? codes.GetValueOrDefault(unitId) : null,
            l.Note)).ToArray();

        return new RecipeResponse(
            recipe.Id, recipe.Title, recipe.Description, recipe.Servings, recipe.PrepMinutes, recipe.CookMinutes,
            recipe.TotalMinutes, [.. steps], ingredients,
            (await LoadTagsAsync(db, [recipe.Id], cancellationToken)).GetValueOrDefault(recipe.Id, []));
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Recipe not found");
}
