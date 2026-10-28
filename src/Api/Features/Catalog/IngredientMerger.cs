using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Catalog;

/// <summary>What a merge moved, and the weeks whose shopping list was regenerated (to notify after the commit).</summary>
public sealed record MergeOutcome(IngredientUsage Moved, IReadOnlyList<DateOnly> RegeneratedWeeks);

/// <summary>
/// Merges one own ingredient into another (T-079): every reference moves to the target, then the source is deleted, all in
/// one transaction with tracked updates only (no <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>). Per reference type:
/// recipe lines get the target id; the pantry row moves, or, when the target already has one, is absorbed into it
/// (newer confirmation, later expiry) and removed; a generated shopping list item moves to the target, or, when the same
/// list already has an item for the target, is removed; then every affected list is regenerated from the plan with the
/// normal reconciler, which recomputes quantities (merged with <c>QuantityMerger</c>, so different dimensions stay separate
/// lines), sections and sources and keeps checked state of the surviving item; open import drafts are rewritten in their JSON.
/// Any failure throws and the transaction is rolled back, leaving everything unchanged.
/// </summary>
public static class IngredientMerger
{
    public static async Task<MergeOutcome> MergeAsync(
        AppDbContext db, Ingredient source, Ingredient target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Recipe lines.
        var lines = await db.RecipeIngredients.Where(l => l.IngredientId == source.Id).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            line.ChangeIngredient(target.Id);
        }

        // Alternatives (T-076): they move too. One that would equal its line's ingredient (the line moves to the target, or already
        // is it) or repeat an alternative the line already has is removed instead.
        var sourceAlternatives = await db.RecipeIngredientAlternatives.Where(a => a.IngredientId == source.Id).ToListAsync(cancellationToken);
        var alternativeLineIds = sourceAlternatives.Select(a => a.RecipeIngredientId).Distinct().ToList();
        var alternativeLines = await db.RecipeIngredients.AsNoTracking().Where(l => alternativeLineIds.Contains(l.Id))
            .Select(l => new { l.Id, l.RecipeId, l.IngredientId }).ToListAsync(cancellationToken);
        var linesWithTargetAlternative = (await db.RecipeIngredientAlternatives.AsNoTracking()
            .Where(a => a.IngredientId == target.Id && alternativeLineIds.Contains(a.RecipeIngredientId))
            .Select(a => a.RecipeIngredientId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var alternative in sourceAlternatives)
        {
            var line = alternativeLines.Single(l => l.Id == alternative.RecipeIngredientId);
            if (line.IngredientId == target.Id || line.IngredientId == source.Id || linesWithTargetAlternative.Contains(line.Id))
            {
                db.RecipeIngredientAlternatives.Remove(alternative);
            }
            else
            {
                alternative.ChangeIngredient(target.Id);
            }
        }

        // A line whose ingredient is the source and that already lists the target as an alternative would read "X or X" after the
        // line moves, which the save validation forbids: that alternative goes.
        var lineIdsMoving = lines.Select(l => l.Id).ToList();
        var targetAlternativesOnMovingLines = await db.RecipeIngredientAlternatives
            .Where(a => a.IngredientId == target.Id && lineIdsMoving.Contains(a.RecipeIngredientId)).ToListAsync(cancellationToken);
        db.RecipeIngredientAlternatives.RemoveRange(targetAlternativesOnMovingLines);

        // Pantry: unique per (household, ingredient), so a clash is combined, not moved.
        var pantry = 0;
        var sourcePantry = await db.PantryItems.FirstOrDefaultAsync(p => p.IngredientId == source.Id, cancellationToken);
        if (sourcePantry is not null)
        {
            pantry = 1;
            var targetPantry = await db.PantryItems.FirstOrDefaultAsync(p => p.IngredientId == target.Id, cancellationToken);
            if (targetPantry is null)
            {
                sourcePantry.ChangeIngredient(target.Id);
            }
            else
            {
                targetPantry.Absorb(sourcePantry);
                db.PantryItems.Remove(sourcePantry);
            }
        }

        // Shopping list items: unique per (list, ingredient) too. Quantities are not copied: the list is regenerated below.
        var sourceItems = await db.ShoppingListItems.Where(i => i.IngredientId == source.Id).ToListAsync(cancellationToken);
        var listIds = sourceItems.Select(i => i.ShoppingListId).Distinct().ToList();
        var listsWithTarget = (await db.ShoppingListItems.AsNoTracking()
            .Where(i => i.IngredientId == target.Id && listIds.Contains(i.ShoppingListId))
            .Select(i => i.ShoppingListId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var item in sourceItems)
        {
            if (listsWithTarget.Contains(item.ShoppingListId))
            {
                db.ShoppingListItems.Remove(item); // the database removes its quantity and source rows
            }
            else
            {
                item.ChangeIngredient(target.Id);
            }
        }

        var weeks = (await db.ShoppingLists.AsNoTracking().Where(l => listIds.Contains(l.Id)).Select(l => l.WeekStart)
            .ToListAsync(cancellationToken)).OrderBy(w => w).ToList();

        // Open import drafts (JSON, no foreign key).
        var drafts = 0;
        foreach (var import in await ReadDraftsAsync(db, cancellationToken))
        {
            var draft = ImportDraftJson.Deserialize(import.DraftJson!);
            if (!draft.References(source.Id))
            {
                continue;
            }

            if (draft.UsesIngredient(source.Id))
            {
                drafts++;
            }

            import.UpdateDraft(ImportDraftJson.Serialize(draft.ReplaceIngredient(source.Id, target.Id)));
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        // Regenerate the affected lists from the plan (the recipe lines moved above are already saved).
        foreach (var week in weeks)
        {
            var generated = await ShoppingListEndpoints.GenerateFromPlanAsync(db, week, now, cancellationToken);
            await ShoppingListEndpoints.ApplyAsync(db, week, generated, cancellationToken);
        }

        var recipes = lines.Select(l => l.RecipeId).Concat(alternativeLines.Select(l => l.RecipeId)).Distinct().Count();
        var tracked = await db.Ingredients.FirstAsync(i => i.Id == source.Id, cancellationToken);
        db.Ingredients.Remove(tracked);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MergeOutcome(new IngredientUsage(recipes, pantry, weeks.Count, drafts), weeks);
    }

    private static Task<List<RecipeImport>> ReadDraftsAsync(AppDbContext db, CancellationToken cancellationToken) =>
        OwnIngredientEndpoints.OpenDrafts(db).ToListAsync(cancellationToken);
}
