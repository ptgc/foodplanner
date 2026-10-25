using Foodplanner.Domain.ShoppingLists.Export;
using Foodplanner.Infrastructure.Export;

namespace Foodplanner.Api.Tests.ShoppingLists;

public sealed class TextShoppingListExporterTests
{
    private static readonly DateOnly Week43 = new(2026, 10, 19);

    private static ShoppingListExportQuantity Q(decimal? amount, string? code, string? name = null) => new(amount, code, name ?? code);

    private static ShoppingListExportItem Item(string name, bool isChecked = false, bool manual = false, params ShoppingListExportQuantity[] quantities) =>
        new(name, manual, isChecked, quantities);

    private static async Task<ShoppingListExportResult> ExportAsync(
        string locale, ShoppingListExportItem[] main, ShoppingListExportItem[]? staples = null) =>
        await new TextShoppingListExporter().ExportAsync(new ShoppingListExport(Week43, locale, main, staples ?? []), CancellationToken.None);

    [Fact]
    public async Task Groups_by_section_main_first_with_bullets_and_quantities_in_nb()
    {
        var result = await ExportAsync(
            "nb",
            [Item("Egg", quantities: Q(5, "stk", "stykk")), Item("Ris", quantities: Q(500, "g", "gram"))],
            [Item("Salt")]);

        Assert.Equal("Å handle\n- Egg 5 stk\n- Ris 500 g\n\nSjekk at du har\n- Salt", result.Text);
        Assert.Equal("Handleliste, uke 43", result.Title);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task English_headings_and_decimal_point()
    {
        var result = await ExportAsync("en", [Item("Milk", quantities: Q(1.5m, "l", "liter"))], [Item("Flour")]);

        Assert.Equal("To buy\n- Milk 1.5 l\n\nCheck you have\n- Flour", result.Text);
        Assert.Equal("Shopping list, week 43", result.Title);
    }

    [Fact]
    public async Task Norwegian_uses_decimal_comma()
    {
        var result = await ExportAsync("nb", [Item("Melk", quantities: Q(1.5m, "l", "liter"))]);

        Assert.Equal("Å handle\n- Melk 1,5 l", result.Text);
    }

    [Fact]
    public async Task Unknown_locale_falls_back_to_english()
    {
        var result = await ExportAsync("de", [Item("Milk")]);

        Assert.Equal("To buy\n- Milk", result.Text);
    }

    [Fact]
    public async Task Checked_items_are_left_out_and_a_section_with_only_checked_items_is_omitted()
    {
        var result = await ExportAsync(
            "en",
            [Item("Rice", isChecked: true, quantities: Q(500, "g")), Item("Egg", quantities: Q(2, "stk"))],
            [Item("Salt", isChecked: true)]);

        Assert.Equal("To buy\n- Egg 2 stk", result.Text);
        Assert.DoesNotContain("Rice", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Check you have", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task To_taste_and_manual_items_with_and_without_quantity()
    {
        var result = await ExportAsync(
            "en",
            [
                Item("Pepper", quantities: Q(null, null)),
                Item("Birthday candles", manual: true, quantities: Q(1, "stk")),
                Item("Coffee filters", manual: true),
            ]);

        Assert.Equal("To buy\n- Pepper\n- Birthday candles 1 stk\n- Coffee filters", result.Text);
    }

    [Fact]
    public async Task One_item_with_several_lines_joins_them_and_non_abbreviated_units_use_their_name()
    {
        var result = await ExportAsync(
            "nb",
            [
                Item("Hvitløk", quantities: Q(2, "fedd", "fedd")),
                Item("Olje", quantities: [Q(1, "ss", "spiseskje"), Q(2, "dl", "desiliter")]),
            ]);

        Assert.Equal("Å handle\n- Hvitløk 2 fedd\n- Olje 1 spiseskje, 2 dl", result.Text);
    }

    [Fact]
    public async Task Keeps_input_order_and_unicode_names_and_has_no_trailing_whitespace()
    {
        ShoppingListExportItem[] main = [Item("  Gulrot  ", quantities: Q(3, "stk")), Item("Ærter"), Item("Øl"), Item("Åker")];

        var first = await ExportAsync("nb", main);
        var second = await ExportAsync("nb", main);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal("Å handle\n- Gulrot 3 stk\n- Ærter\n- Øl\n- Åker", first.Text);
        Assert.All(first.Text!.Split('\n'), line => Assert.Equal(line.TrimEnd(), line));
        Assert.DoesNotContain('\r', first.Text);
    }

    [Fact]
    public async Task Empty_or_fully_checked_list_gives_a_short_localized_sentence()
    {
        Assert.Equal("Nothing to buy.", (await ExportAsync("en", [])).Text);
        Assert.Equal("Ingenting å handle.", (await ExportAsync("nb", [Item("Ris", isChecked: true)])).Text);
    }

    [Fact]
    public void Selector_finds_exporters_by_name_ignoring_case()
    {
        var selector = new ShoppingListExporterSelector([new TextShoppingListExporter()]);

        Assert.IsType<TextShoppingListExporter>(selector.Find("TEXT"));
        Assert.Null(selector.Find("oda"));
        Assert.Null(selector.Find(null));
    }
}
