using System.Text.Json.Serialization;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Recipes;

/// <summary>An ingredient that can fill a line: the line's own ingredient (first) or one of its alternatives. A staple counts as at home.</summary>
public sealed record LineOption(Guid IngredientId, bool IsStaple);

/// <summary>
/// One ingredient line of a recipe as the pantry matcher needs it (T-100). <see cref="Options"/> has the line's own ingredient first
/// and then its alternatives (at least one). A Measured line has a <see cref="Quantity"/> with an amount, for the recipe's own
/// servings; ToTaste and AsNeeded lines have none.
/// </summary>
public sealed record RecipeLine
{
    public RecipeLine(
        IReadOnlyList<LineOption> options,
        AmountKind kind,
        Quantity? quantity,
        IngredientPurpose purpose = IngredientPurpose.Main,
        bool isOptional = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("A line needs at least its own ingredient.", nameof(options));
        }

        if ((kind == AmountKind.Measured) != (quantity?.Amount is not null))
        {
            throw new ArgumentException("Only a Measured line has an amount.", nameof(quantity));
        }

        Options = options;
        Kind = kind;
        Quantity = quantity;
        Purpose = purpose;
        IsOptional = isOptional;
    }

    public IReadOnlyList<LineOption> Options { get; }

    public AmountKind Kind { get; }

    public Quantity? Quantity { get; }

    public IngredientPurpose Purpose { get; }

    public bool IsOptional { get; }

    public Guid IngredientId => Options[0].IngredientId;
}

/// <summary>
/// A recipe as a candidate for "what can I make". <see cref="KidLikes"/> is how many kid profiles like it (T-026);
/// <see cref="LastPlannedOn"/> is the latest date the recipe was on a meal plan (the nearest thing to "cooked", the model has no
/// cooked flag), or null.
/// </summary>
public sealed record RecipeCandidate(
    Guid RecipeId,
    string Title,
    FoodType FoodType,
    int Servings,
    IReadOnlyList<RecipeLine> Lines,
    int KidLikes = 0,
    DateOnly? LastPlannedOn = null);

/// <summary>Why an ingredient counts as missing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MissingReason
{
    /// <summary>The pantry has no active entry for it (or for any alternative).</summary>
    NotAtHome,

    /// <summary>The pantry has it with an amount that is less than the recipe needs; <see cref="MissingIngredient.Quantities"/> is the shortfall.</summary>
    NotEnough,

    /// <summary>The pantry amount is in a unit that cannot be compared (mass vs volume without a density, or another count unit); the whole need is missing.</summary>
    UnitMismatch,
}

/// <summary>
/// One missing thing. <see cref="Quantities"/> is what is missing (the whole need, or the shortfall) in display units, rounded;
/// <see cref="AlternativeIds"/> are the other ingredients that would have done.
/// </summary>
public sealed record MissingIngredient(
    Guid IngredientId, IReadOnlyList<Quantity> Quantities, MissingReason Reason, IReadOnlyList<Guid> AlternativeIds);

/// <summary>A recipe with what it lacks at the chosen servings. <see cref="MissingCount"/> is the number of distinct ingredients missing.</summary>
public sealed record RecipeMatch(RecipeCandidate Recipe, int Servings, IReadOnlyList<MissingIngredient> Missing)
{
    public int MissingCount => Missing.Count;
}

/// <summary>
/// Pure: which recipes can the household cook with what is in the pantry now (T-100, Q-029).
/// Per recipe, at the chosen servings (default: the recipe's own), a line is NEVER missing when it is optional, ToTaste or
/// AsNeeded, a small amount of frying fat (<see cref="ShoppingListGenerator.NeedsBuying(AmountKind, IngredientPurpose, bool, Quantity)"/>,
/// the shopping list's rule), or when any of its options (the ingredient or an alternative) is a staple. Every other line needs
/// one of its options at home, by the pantry entries that are active at <c>now</c> (an expired entry does not count):
///  - an entry without an amount ("we have some") satisfies any amount;
///  - an entry with an amount must cover the scaled amount in the same dimension (Mass with Mass, Volume with Volume, a count
///    unit only with itself); Mass and Volume convert only with a density for the ingredient (grams per millilitre, passed in);
///    a shortfall is missing by exactly the shortfall (<see cref="MissingReason.NotEnough"/>);
///  - an amount in a unit that cannot be compared (no density, another count unit) is missing in full (<see cref="MissingReason.UnitMismatch"/>);
///  - one pantry amount is used up across the recipe's lines (two lines of 100 g cheese need 200 g, not 100 g twice).
/// A line with alternatives is satisfied by the first option that is at home; when none is, the line's own ingredient is reported
/// missing, with the alternatives listed. Two missing lines of one ingredient are one missing thing. A recipe with no ingredient
/// lines is never suggested. Only recipes missing at most <see cref="MaxMissing"/> things are returned.
/// Ranking: fewest missing, then most kid likes, then not planned in the last <see cref="RecentDays"/> days, then title
/// (OrdinalIgnoreCase, Ordinal), then id. The week's meal mix rules (T-090) are not applied.
/// </summary>
public static class PantryRecipeMatcher
{
    public const int MaxMissing = 2;
    public const int RecentDays = 14;

    public static IReadOnlyList<RecipeMatch> Suggest(
        IEnumerable<RecipeCandidate> recipes,
        IEnumerable<PantryEntry> pantry,
        DateTimeOffset now,
        DateOnly today,
        UnitCatalog catalog,
        int? servings = null,
        IReadOnlyDictionary<Guid, decimal>? densities = null)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(pantry);
        var entries = Active(pantry, now);
        return [.. recipes
            .Where(r => r.Lines.Count > 0)
            .Select(r => MatchActive(r, entries, servings ?? r.Servings, catalog, densities))
            .Where(m => m.MissingCount <= MaxMissing)
            .OrderBy(m => m.MissingCount)
            .ThenByDescending(m => m.Recipe.KidLikes)
            .ThenBy(m => IsRecent(m.Recipe, today))
            .ThenBy(m => m.Recipe.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Recipe.Title, StringComparer.Ordinal)
            .ThenBy(m => m.Recipe.RecipeId)];
    }

    /// <summary>One recipe, whatever it lacks (no cutoff): what the "add the missing items" action needs.</summary>
    public static RecipeMatch Match(
        RecipeCandidate recipe,
        IEnumerable<PantryEntry> pantry,
        DateTimeOffset now,
        UnitCatalog catalog,
        int? servings = null,
        IReadOnlyDictionary<Guid, decimal>? densities = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(pantry);
        return MatchActive(recipe, Active(pantry, now), servings ?? recipe.Servings, catalog, densities);
    }

    /// <summary>Planned today or in the last <see cref="RecentDays"/> days (a future date does not count).</summary>
    public static bool IsRecent(RecipeCandidate recipe, DateOnly today) =>
        recipe.LastPlannedOn is { } last && last >= today.AddDays(-RecentDays) && last <= today;

    private static Dictionary<Guid, PantryEntry> Active(IEnumerable<PantryEntry> pantry, DateTimeOffset now) =>
        pantry.Where(p => p.IsActive(now)).GroupBy(p => p.IngredientId).ToDictionary(g => g.Key, g => g.First());

    private static RecipeMatch MatchActive(
        RecipeCandidate recipe, Dictionary<Guid, PantryEntry> pantry, int servings, UnitCatalog catalog, IReadOnlyDictionary<Guid, decimal>? densities)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(servings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipe.Servings);

        var remaining = new Dictionary<Guid, Stock>();
        foreach (var (id, entry) in pantry)
        {
            if (entry.Amount is { Amount: { } amount } quantity)
            {
                remaining[id] = new Stock(quantity.Unit, amount * quantity.Unit.FactorToBase);
            }
        }

        var missing = new List<(Guid Id, Quantity Quantity, MissingReason Reason, IReadOnlyList<Guid> Alternatives)>();
        foreach (var line in recipe.Lines)
        {
            if (line.Quantity is not { Amount: not null } baseQuantity)
            {
                continue; // ToTaste and AsNeeded: never missing
            }

            var need = RecipeScaling.Scale(line.Kind, baseQuantity, recipe.Servings, servings);
            if (!ShoppingListGenerator.NeedsBuying(line.Kind, line.Purpose, line.IsOptional, need) || line.Options.Any(o => o.IsStaple))
            {
                continue;
            }

            LineResult? first = null;
            var satisfied = false;
            foreach (var option in line.Options)
            {
                var result = Check(option.IngredientId, need, pantry, remaining, densities);
                first ??= result;
                if (result.Reason is null)
                {
                    satisfied = true;
                    break;
                }

                if (result.Reason == MissingReason.NotEnough)
                {
                    remaining[option.IngredientId] = remaining[option.IngredientId] with { BaseAmount = 0m }; // used up by this line
                }
            }

            if (!satisfied)
            {
                missing.Add((line.IngredientId, first!.Shortfall!, first.Reason!.Value, [.. line.Options.Skip(1).Select(o => o.IngredientId)]));
            }
        }

        var grouped = missing
            .GroupBy(m => m.Id)
            .Select(g => new MissingIngredient(
                g.Key,
                QuantityMerger.MergeForDisplay(g.Select(m => m.Quantity), catalog),
                g.First().Reason,
                g.First().Alternatives))
            .ToList();
        return new RecipeMatch(recipe, servings, grouped);
    }

    /// <summary>Is <paramref name="need"/> covered by this ingredient at home? Uses up what it covers.</summary>
    private static LineResult Check(
        Guid ingredientId, Quantity need, Dictionary<Guid, PantryEntry> pantry, Dictionary<Guid, Stock> remaining,
        IReadOnlyDictionary<Guid, decimal>? densities)
    {
        if (!pantry.TryGetValue(ingredientId, out _))
        {
            return new LineResult(MissingReason.NotAtHome, need);
        }

        if (!remaining.TryGetValue(ingredientId, out var stock))
        {
            return new LineResult(null, null); // "we have some": satisfies any amount
        }

        var density = densities is not null && densities.TryGetValue(ingredientId, out var d) && d > 0 ? d : (decimal?)null;
        var available = AvailableIn(stock, need.Unit, density);
        if (available is not { } have)
        {
            return new LineResult(MissingReason.UnitMismatch, need);
        }

        var needBase = need.BaseAmount!.Value;
        if (have >= needBase)
        {
            remaining[ingredientId] = stock with { BaseAmount = Take(stock, need.Unit, needBase, density) };
            return new LineResult(null, null);
        }

        return new LineResult(MissingReason.NotEnough, new Quantity((needBase - have) / need.Unit.FactorToBase, need.Unit));
    }

    /// <summary>What is left in stock, expressed in the base amount of <paramref name="target"/>'s dimension; null when not comparable.</summary>
    private static decimal? AvailableIn(Stock stock, Unit target, decimal? density)
    {
        if (UnitConversion.CanConvert(stock.Unit, target))
        {
            return stock.BaseAmount;
        }

        return (stock.Unit.Dimension, target.Dimension, density) switch
        {
            (UnitDimension.Volume, UnitDimension.Mass, { } d) => stock.BaseAmount * d,
            (UnitDimension.Mass, UnitDimension.Volume, { } d) => stock.BaseAmount / d,
            _ => null,
        };
    }

    /// <summary>The stock's base amount after using <paramref name="needBase"/> (in the target's dimension).</summary>
    private static decimal Take(Stock stock, Unit target, decimal needBase, decimal? density)
    {
        if (UnitConversion.CanConvert(stock.Unit, target))
        {
            return stock.BaseAmount - needBase;
        }

        var used = stock.Unit.Dimension == UnitDimension.Volume ? needBase / density!.Value : needBase * density!.Value;
        return Math.Max(0m, stock.BaseAmount - used);
    }

    private sealed record Stock(Unit Unit, decimal BaseAmount);

    private sealed record LineResult(MissingReason? Reason, Quantity? Shortfall);
}
