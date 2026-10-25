using System.Globalization;
using Foodplanner.Domain.Localization;
using Foodplanner.Domain.ShoppingLists.Export;

namespace Foodplanner.Infrastructure.Export;

/// <summary>
/// The share/copy export (T-050): plain text, UTF-8, LF line breaks, no trailing whitespace. It needs no provider and
/// works whatever happens with Oda.
/// <list type="bullet">
/// <item>Sections in order: the main list, then "check you have" (kept as a short reminder section). Empty sections are omitted.</item>
/// <item>One item per line as a Markdown-style bullet: <c>- Ris 500 g</c>. Several lines of one item (a mass and a volume): <c>- Melk 1 l, 2 stk</c>. To taste or no quantity: the name alone.</item>
/// <item>Checked items are left out; the checked state is never part of the text.</item>
/// <item>Order is the order of the input (the list's display order), so the output is deterministic.</item>
/// <item>Units: the code for g, kg, ml, dl, l and stk ("500 g", "5 stk"), the unit's name in the list's language for the rest ("2 teskje", "1 teaspoon"). Numbers use the decimal comma for nb and the point for en. Headings come from a small table per locale; unknown locales use English.</item>
/// </list>
/// </summary>
public sealed class TextShoppingListExporter : IShoppingListExporter
{
    public const string ExporterName = "text";

    private sealed record Words(string Title, string Main, string CheckYouHave, string Empty);

    private static readonly Dictionary<string, Words> Table = new(StringComparer.Ordinal)
    {
        [Locales.English] = new("Shopping list, week", "To buy", "Check you have", "Nothing to buy."),
        [Locales.Norwegian] = new("Handleliste, uke", "Å handle", "Sjekk at du har", "Ingenting å handle."),
    };

    private static readonly HashSet<string> Abbreviated = new(["g", "kg", "ml", "dl", "l", "stk"], StringComparer.Ordinal);

    public string Name => ExporterName;

    public Task<ShoppingListExportResult> ExportAsync(ShoppingListExport list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        var words = Table.GetValueOrDefault(list.Locale) ?? Table[Locales.Default];
        var comma = list.Locale == Locales.Norwegian;
        var title = $"{words.Title} {ISOWeek.GetWeekOfYear(list.WeekStart.ToDateTime(TimeOnly.MinValue))}";

        var sections = new List<string>();
        AddSection(sections, words.Main, list.Main, comma);
        AddSection(sections, words.CheckYouHave, list.CheckYouHave, comma);
        var text = sections.Count == 0 ? words.Empty : string.Join("\n\n", sections);
        return Task.FromResult(new ShoppingListExportResult(title, text));
    }

    private static void AddSection(List<string> sections, string heading, IReadOnlyList<ShoppingListExportItem> items, bool comma)
    {
        var lines = items.Where(i => !i.Checked).Select(i => "- " + Line(i, comma)).ToList();
        if (lines.Count > 0)
        {
            sections.Add(heading + "\n" + string.Join("\n", lines));
        }
    }

    private static string Line(ShoppingListExportItem item, bool comma)
    {
        var quantities = item.Quantities
            .Where(q => q.Amount is not null)
            .Select(q => Quantity(q, comma))
            .ToList();
        return (quantities.Count == 0 ? item.Name.Trim() : $"{item.Name.Trim()} {string.Join(", ", quantities)}").TrimEnd();
    }

    private static string Quantity(ShoppingListExportQuantity q, bool comma)
    {
        var amount = q.Amount!.Value.ToString("0.####", CultureInfo.InvariantCulture);
        if (comma)
        {
            amount = amount.Replace('.', ',');
        }

        // Metric codes and stk read the same in both languages and are short; the others (ts, ss, kopp, fedd, klype) use the translated name.
        var unit = q.UnitCode is not null && Abbreviated.Contains(q.UnitCode) ? q.UnitCode
            : string.IsNullOrWhiteSpace(q.UnitName) ? q.UnitCode : q.UnitName;
        return string.IsNullOrWhiteSpace(unit) ? amount : $"{amount} {unit.Trim()}";
    }
}
