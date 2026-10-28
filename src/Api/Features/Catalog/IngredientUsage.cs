using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Catalog;

/// <summary>Counts of where an ingredient is referenced (T-079), for the "in use" answer and the management list.</summary>
public readonly record struct IngredientUsage(int Recipes, int PantryItems, int ShoppingLists, int ImportDrafts)
{
    public bool Any => Recipes + PantryItems + ShoppingLists + ImportDrafts > 0;

    public IngredientUsageResponse ToResponse() => new(Recipes, PantryItems, ShoppingLists, ImportDrafts);

    /// <summary>
    /// Usage of each id, through the tenant-filtered sets (so only this household's references count). Recipes and
    /// shopping lists are counted as distinct recipes and lists; import drafts are read from the stored JSON of the
    /// household's open drafts (at most 50), which the database cannot see. An id with no references maps to zeros.
    /// </summary>
    public static async Task<Dictionary<Guid, IngredientUsage>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken)
    {
        var recipes = (await db.RecipeIngredients.AsNoTracking().Select(l => new { l.IngredientId, l.RecipeId }).Distinct()
            .ToListAsync(cancellationToken)).GroupBy(x => x.IngredientId).ToDictionary(g => g.Key, g => g.Count());
        var pantry = (await db.PantryItems.AsNoTracking().Select(p => p.IngredientId).ToListAsync(cancellationToken))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var lists = (await db.ShoppingListItems.AsNoTracking().Where(i => i.IngredientId != null)
            .Select(i => new { IngredientId = i.IngredientId!.Value, i.ShoppingListId }).Distinct().ToListAsync(cancellationToken))
            .GroupBy(x => x.IngredientId).ToDictionary(g => g.Key, g => g.Count());

        var drafts = new Dictionary<Guid, int>();
        var json = await OwnIngredientEndpoints.OpenDrafts(db).AsNoTracking().Select(i => i.DraftJson!).ToListAsync(cancellationToken);
        foreach (var text in json)
        {
            foreach (var id in ImportDraftJson.Deserialize(text).Lines.Select(l => l.IngredientId).OfType<Guid>().Distinct())
            {
                drafts[id] = drafts.GetValueOrDefault(id) + 1;
            }
        }

        return ingredientIds.Distinct().ToDictionary(
            id => id,
            id => new IngredientUsage(
                recipes.GetValueOrDefault(id), pantry.GetValueOrDefault(id), lists.GetValueOrDefault(id), drafts.GetValueOrDefault(id)));
    }
}
