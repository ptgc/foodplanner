using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Recipes;

/// <summary>
/// The database side of the food type (T-089): gathers the protein classes and hands them to the pure
/// <see cref="FoodTypeDeriver"/>. All reads go through the filtered sets, so another household's own ingredient is never seen.
/// </summary>
internal static class RecipeFoodTypes
{
    /// <summary>The derived type of the ingredient lines of a (validated) request.</summary>
    public static async Task<FoodType> DeriveForRequestAsync(AppDbContext db, RecipeRequest request, CancellationToken cancellationToken)
    {
        var lines = request.Ingredients ?? [];
        var ids = lines.Select(l => l.IngredientId!.Value).Distinct().ToList();
        var classes = ids.Count == 0
            ? []
            : await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ProteinClass, cancellationToken);
        return FoodTypeDeriver.Derive(lines.Select(l => new FoodTypeLine(
            classes.GetValueOrDefault(l.IngredientId!.Value, ProteinClass.Unknown),
            IngredientPurposes.TryParse(l.Purpose, out var purpose) ? purpose : IngredientPurpose.Main,
            l.IsOptional ?? false)));
    }

    /// <summary>
    /// Re-derives the type of the given recipes after their ingredients (or an ingredient's protein class) changed. Recipes with a
    /// manual type are left alone. Tracked updates only; the caller saves.
    /// </summary>
    public static async Task RecomputeAsync(AppDbContext db, IReadOnlyCollection<Guid> recipeIds, CancellationToken cancellationToken)
    {
        if (recipeIds.Count == 0)
        {
            return;
        }

        var recipes = await db.Recipes.Where(r => recipeIds.Contains(r.Id) && !r.FoodTypeIsManual).ToListAsync(cancellationToken);
        if (recipes.Count == 0)
        {
            return;
        }

        var ids = recipes.Select(r => r.Id).ToList();
        var lines = await db.RecipeIngredients.AsNoTracking().Where(l => ids.Contains(l.RecipeId))
            .Select(l => new { l.RecipeId, l.IngredientId, l.Purpose, l.IsOptional }).ToListAsync(cancellationToken);
        var ingredientIds = lines.Select(l => l.IngredientId).Distinct().ToList();
        var classes = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.ProteinClass, cancellationToken);
        var byRecipe = lines.ToLookup(l => l.RecipeId);
        foreach (var recipe in recipes)
        {
            recipe.ApplyDerivedFoodType(FoodTypeDeriver.Derive(byRecipe[recipe.Id].Select(l => new FoodTypeLine(
                classes.GetValueOrDefault(l.IngredientId, ProteinClass.Unknown), l.Purpose, l.IsOptional))));
        }
    }
}
