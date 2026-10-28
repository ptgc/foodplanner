namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// The "or ..." shown on a shopping list item (T-076): the alternatives of every recipe line that contributes to it.
/// Pure. The shopping list stores no alternatives; the API reads them from the contributing recipes' lines when it shows
/// the list.
/// </summary>
public static class ShoppingAlternatives
{
    /// <summary>
    /// The union of the lines' alternative ids in first-seen order, without duplicates and without the item's own
    /// ingredient (a line "smør eller smør" or another recipe that lists the item itself as an alternative adds nothing).
    /// </summary>
    public static IReadOnlyList<Guid> For(Guid ingredientId, IEnumerable<IEnumerable<Guid>> alternativesPerLine)
    {
        ArgumentNullException.ThrowIfNull(alternativesPerLine);
        var seen = new HashSet<Guid> { ingredientId };
        var result = new List<Guid>();
        foreach (var id in alternativesPerLine.SelectMany(line => line))
        {
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
