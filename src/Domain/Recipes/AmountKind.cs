using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// What kind of amount an ingredient line has (T-076, Q-018). "To taste" is an explicit kind, not a missing amount.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AmountKind
{
    /// <summary>A quantity and a unit ("2 dl melk"). The only kind that has them.</summary>
    Measured,

    /// <summary>"Etter smak" / "to taste": on the prep list, but not an amount and never scaled or bought by amount.</summary>
    ToTaste,

    /// <summary>"Til steking", "til pensling" without an amount: as much as the cook needs.</summary>
    AsNeeded,
}

/// <summary>What the ingredient is for. How it is used stays in the steps; this only groups the prep list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IngredientPurpose
{
    Main,
    Frying,
    Serving,
    Garnish,
    Seasoning,
}

public enum AmountProblem
{
    UnknownKind,
    QuantityRequired,
    UnitRequired,
    QuantityForbidden,
    UnitForbidden,
}

/// <summary>The one place that says which combinations of kind, quantity and unit are valid (CLAUDE.md rule 3).</summary>
public static class AmountRules
{
    /// <summary>Infers the kind of a line that does not say: a quantity means Measured, none means ToTaste.</summary>
    public static AmountKind Infer(decimal? quantity) => quantity is null ? AmountKind.ToTaste : AmountKind.Measured;

    /// <summary>
    /// Null when valid. Measured needs a quantity and a unit; ToTaste and AsNeeded forbid both. The range of the quantity
    /// is checked separately.
    /// </summary>
    public static AmountProblem? Check(AmountKind kind, decimal? quantity, Guid? unitId)
    {
        if (!Enum.IsDefined(kind))
        {
            return AmountProblem.UnknownKind;
        }

        if (kind == AmountKind.Measured)
        {
            if (quantity is null)
            {
                return AmountProblem.QuantityRequired;
            }

            return unitId is null || unitId == Guid.Empty ? AmountProblem.UnitRequired : null;
        }

        if (quantity is not null)
        {
            return AmountProblem.QuantityForbidden;
        }

        return unitId is not null ? AmountProblem.UnitForbidden : null;
    }
}

public static class AmountKinds
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers.</summary>
    public static bool TryParse(string? value, out AmountKind kind)
    {
        foreach (var candidate in Enum.GetValues<AmountKind>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

public static class IngredientPurposes
{
    /// <summary>Strict parse: exact names only (case-insensitive), no numbers.</summary>
    public static bool TryParse(string? value, out IngredientPurpose purpose)
    {
        foreach (var candidate in Enum.GetValues<IngredientPurpose>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                purpose = candidate;
                return true;
            }
        }

        purpose = default;
        return false;
    }
}
