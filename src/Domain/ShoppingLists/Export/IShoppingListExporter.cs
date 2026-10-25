namespace Foodplanner.Domain.ShoppingLists.Export;

/// <summary>
/// The port for sending a shopping list somewhere (ADR 0008): plain text to share or copy today, a provider cart
/// (Oda, T-053) later. Implementations live outside Domain (Infrastructure) and are registered in DI; select one by
/// <see cref="Name"/>. Domain only knows this interface and the provider-neutral <see cref="ShoppingListExport"/>.
/// </summary>
public interface IShoppingListExporter
{
    /// <summary>The stable selector, lower case (for example <c>text</c>).</summary>
    string Name { get; }

    Task<ShoppingListExportResult> ExportAsync(ShoppingListExport list, CancellationToken cancellationToken);
}

/// <summary>
/// What an export produced. A text exporter fills <see cref="Text"/>; a provider exporter may fill <see cref="Url"/>
/// (a cart to open) instead. <see cref="Title"/> is a short heading for a share sheet.
/// </summary>
public sealed record ShoppingListExportResult(string Title, string? Text = null, Uri? Url = null);
