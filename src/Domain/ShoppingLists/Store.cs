using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// A store the household shops at (T-087, Q-019), for example "Rema Lade", with the order in which it walks the store sections.
/// The order names every <see cref="StoreSection"/> exactly once. Name is unique per household, ignoring case. A member picks the
/// store while shopping (<see cref="Membership.ChosenStoreId"/>) and the list is then sorted by this order.
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class Store : Entity, ITenantEntity
{
    public const int MaxNameLength = 100;
    public const int MaxPerHousehold = 20;

    private Store()
    {
    }

    public Store(string name, IReadOnlyList<StoreSection>? sectionOrder = null)
    {
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid store name.", nameof(name));
        SetSectionOrder(sectionOrder ?? StoreSectionOrder.Default);
    }

    public Guid HouseholdId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The persisted form of the order: section names separated by commas.</summary>
    public string SectionOrderText { get; private set; } = string.Empty;

    /// <summary>The sections in the order this store is walked. Always every section once.</summary>
    public IReadOnlyList<StoreSection> SectionOrder =>
        SectionOrderText.Length == 0
            ? StoreSectionOrder.Default
            : StoreSectionOrder.Effective([.. SectionOrderText.Split(',').Select(n => StoreSections.TryParse(n, out var s) ? s : StoreSection.Other)]);

    public void Rename(string name) =>
        Name = NormalizeName(name) ?? throw new ArgumentException("Invalid store name.", nameof(name));

    public void SetSectionOrder(IReadOnlyList<StoreSection> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!StoreSectionOrder.IsComplete(order))
        {
            throw new ArgumentException("A store order names every section exactly once.", nameof(order));
        }

        SectionOrderText = string.Join(',', order);
    }

    /// <summary>Trims and collapses inner whitespace; null when empty, too long or containing control characters.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is 0 or > MaxNameLength || collapsed.Any(char.IsControl) ? null : collapsed;
    }
}
