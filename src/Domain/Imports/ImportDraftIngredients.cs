namespace Foodplanner.Domain.Imports;

/// <summary>Ingredient references inside a stored draft (T-079): the draft is JSON, so the database cannot see them.</summary>
public static class ImportDraftIngredients
{
    /// <summary>Whether a line uses the ingredient (it is "in use" by the draft).</summary>
    public static bool UsesIngredient(this ImportDraft draft, Guid ingredientId) => draft.Lines.Any(l => l.IngredientId == ingredientId);

    /// <summary>Whether <see cref="ReplaceIngredient"/> would change anything: a line uses the ingredient or a suggestion names it.</summary>
    public static bool References(this ImportDraft draft, Guid ingredientId) =>
        draft.Lines.Any(l => l.IngredientId == ingredientId || l.Suggestions.Any(s => s.IngredientId == ingredientId));

    /// <summary>
    /// The draft with <paramref name="from"/> replaced by <paramref name="to"/> on every line (merging ingredients, T-079).
    /// Suggestions naming <paramref name="from"/> are dropped (they are hints and the line now has its match). With a
    /// null <paramref name="to"/> only the suggestions are dropped, and a line that still uses <paramref name="from"/> is an error.
    /// </summary>
    public static ImportDraft ReplaceIngredient(this ImportDraft draft, Guid from, Guid? to)
    {
        if (to is null && draft.UsesIngredient(from))
        {
            throw new InvalidOperationException("A line still uses the ingredient.");
        }

        var lines = draft.Lines.Select(l => l with
        {
            IngredientId = l.IngredientId == from ? to : l.IngredientId,
            Suggestions = [.. l.Suggestions.Where(s => s.IngredientId != from)],
        }).ToList();
        return draft with { Lines = lines };
    }
}
