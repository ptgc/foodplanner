using Foodplanner.Api.Authorization;
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
    RecipeIngredientRequest[]? Ingredients);

/// <summary>A grid card: enough to list recipes. <c>TotalMinutes</c> is prep plus cook, null when neither is set.</summary>
public sealed record RecipeSummaryResponse(Guid Id, string Title, int Servings, int? TotalMinutes);

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
    RecipeIngredientResponse[] Ingredients);

/// <summary>Recipe CRUD (T-021). Any member reads; only an Adult or Owner writes.</summary>
public static class RecipeEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxSearchLength = 100;

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

    /// <summary>Recipes of the route's household (the query filter), sorted by title; <c>search</c> is a case-insensitive "contains" on the title.</summary>
    private static async Task<IResult> ListRecipes(
        string? search, int? limit, int? offset, AppDbContext db, CancellationToken cancellationToken)
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

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Recipes.AsNoTracking();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(r => r.Title.Contains(term));
        }

        var take = limit ?? DefaultLimit;
        var skip = offset ?? 0;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(r => r.Title).ThenBy(r => r.Id).Skip(skip).Take(take)
            .Select(r => new { r.Id, r.Title, r.Servings, r.PrepMinutes, r.CookMinutes })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new RecipeSummaryResponse(
            r.Id, r.Title, r.Servings, r.PrepMinutes is null && r.CookMinutes is null ? null : (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0))).ToArray();
        return Results.Ok(new RecipeListResponse(items, total, take, skip));
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
            recipe.TotalMinutes, [.. steps], ingredients);
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Recipe not found");
}
