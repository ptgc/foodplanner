using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// A suggestion of a regular grocery that a household dismissed (T-110, Q-030): the normalised label (<see cref="GroceryLabel"/>)
/// stays hidden until <see cref="SuggestionRules.HiddenWeeks"/> weeks after <see cref="DismissedAt"/>. One row per household and
/// label (unique index); dismissing again renews the time. The label is free text typed by a person: it is never logged.
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class DismissedGrocerySuggestion : Entity, ITenantEntity
{
    /// <summary>A soft cap on stored dismissals per household (expired ones are removed whenever someone dismisses).</summary>
    public const int MaxPerHousehold = 500;

    private DismissedGrocerySuggestion()
    {
    }

    public DismissedGrocerySuggestion(string label, DateTimeOffset dismissedAt)
    {
        if (string.IsNullOrEmpty(label) || label.Length > GroceryLabel.MaxKeyLength || label.Trim() != label)
        {
            throw new ArgumentException("A dismissed suggestion needs a key from GroceryLabel.Normalize of 1-100 characters.", nameof(label));
        }

        Label = label;
        DismissedAt = dismissedAt;
    }

    public Guid HouseholdId { get; private set; }

    /// <summary>The normalised label (the comparison key), not the spelling someone typed.</summary>
    public string Label { get; private set; } = string.Empty;

    public DateTimeOffset DismissedAt { get; private set; }

    public void Renew(DateTimeOffset dismissedAt) => DismissedAt = dismissedAt;

    /// <summary>When the dismissal stops hiding the suggestion.</summary>
    public DateTimeOffset HiddenUntil(SuggestionRules? rules = null) =>
        DismissedAt.AddDays(7 * (rules ?? SuggestionRules.Default).HiddenWeeks);
}
