namespace Foodplanner.Domain.ShoppingLists.Export;

/// <summary>
/// The provider-neutral input of a shopping list export (T-050, ADR 0008): what a person sees on the list, in one
/// language. No provider types, no ids that only matter to the database. Quantities are display quantities (already
/// merged and rounded); do not merge or scale them again.
/// </summary>
/// <param name="WeekStart">The Monday of the list's ISO week.</param>
/// <param name="Locale">The language of the names (<c>nb</c> or <c>en</c>); exporters use it for their own words.</param>
/// <param name="Main">The main list, in display order.</param>
/// <param name="CheckYouHave">The staples to check at home, in display order.</param>
public sealed record ShoppingListExport(
    DateOnly WeekStart,
    string Locale,
    IReadOnlyList<ShoppingListExportItem> Main,
    IReadOnlyList<ShoppingListExportItem> CheckYouHave);

/// <summary>An item as shown: name in the requested language (or the manual label as typed), its lines and its checked flag.</summary>
public sealed record ShoppingListExportItem(
    string Name, bool IsManual, bool Checked, IReadOnlyList<ShoppingListExportQuantity> Quantities);

/// <summary>An amount with its unit; both null means "to taste". <c>UnitName</c> is in the list's language, <c>UnitCode</c> is stable.</summary>
public sealed record ShoppingListExportQuantity(decimal? Amount, string? UnitCode, string? UnitName);
