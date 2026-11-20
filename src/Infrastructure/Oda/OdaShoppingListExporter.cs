using System.Globalization;
using Foodplanner.Domain.Localization;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.ShoppingLists.Export;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// The Oda exporter (name <c>oda</c>, T-050's selector, T-053). Exporting here only <i>summarises</i> what would be sent to the Oda
/// cart: the unchecked items of the "To buy" section by name (the same selection the cart fill uses). Sending needs the person to
/// sign in at Oda, so it is the two-step flow of <c>oda/start</c> and <c>oda/cart</c>, never this call, and this call never
/// contacts Oda. <see cref="ShoppingListExportResult.Url"/> is the cart link, which only exists after a fill, so it is null here.
/// </summary>
public sealed class OdaShoppingListExporter : IShoppingListExporter
{
    public const string ExporterName = "oda";

    public string Name => ExporterName;

    public Task<ShoppingListExportResult> ExportAsync(ShoppingListExport list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        var lines = CartLineSelector.Select(list.Main.Select(i => new CartSourceItem(Guid.Empty, i.Name, i.Checked)));
        var norwegian = list.Locale == Locales.Norwegian;
        var title = $"{(norwegian ? "Oda-handlekurv, uke" : "Oda cart, week")} {ISOWeek.GetWeekOfYear(list.WeekStart.ToDateTime(TimeOnly.MinValue))}";
        var heading = norwegian ? "Legges i handlekurven (én pakke av hver)" : "Added to the cart (one pack of each)";
        var text = lines.Count == 0
            ? (norwegian ? "Ingenting å legge i handlekurven." : "Nothing to add to the cart.")
            : heading + "\n" + string.Join("\n", lines.Select(l => "- " + l.Name));
        return Task.FromResult(new ShoppingListExportResult(title, text));
    }
}
