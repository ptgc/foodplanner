namespace Foodplanner.Domain.Catalog;

/// <summary>
/// A catalog row with a display name (ingredient, unit, tag). For a global row <see cref="Name"/> is the English name,
/// the last fallback; its other names are <see cref="CatalogTranslation"/> rows. For a household's own row it is the text
/// the household typed and is shown as it is in every locale.
/// </summary>
public interface ICatalogNamed
{
    Guid Id { get; }

    string Name { get; }
}
