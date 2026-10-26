using System.Globalization;
using Foodplanner.Domain.Imports;

namespace Foodplanner.Domain.Tests.Imports;

public class IngredientLineParserTests
{
    /// <summary>
    /// "[low..]quantity|unit|ingredient|note". Quantity and unit are "-" when absent; a unit with a trailing * was implied
    /// (a quantity without a unit word means stk); a unit that starts with ? is a unit word the catalog does not have.
    /// </summary>
    private static string Show(ParsedIngredientLine p)
    {
        Assert.Equal(ParsedLineKind.Parsed, p.Kind);
        var quantity = p.Quantity is null ? "-" : (p.QuantityLow is { } low ? Num(low) + ".." : string.Empty) + Num(p.Quantity.Value);
        var unit = p.UnitText is not null ? "?" + p.UnitText : p.UnitCode is null ? "-" : p.UnitCode + (p.UnitImplied ? "*" : string.Empty);
        return $"{quantity}|{unit}|{p.IngredientText}|{p.Note}";
    }

    private static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    [Theory]
    // Norwegian
    [InlineData("2 dl melk", "2|dl|melk|")]
    [InlineData("1/2 ts salt", "0.5|ts|salt|")]
    [InlineData("200 g smør, myk", "200|g|smør|myk")]
    [InlineData("2-3 fedd hvitløk", "2..3|fedd|hvitløk|2-3")]
    [InlineData("3 egg", "3|stk*|egg|")]
    [InlineData("3 stk. egg", "3|stk|egg|")]
    [InlineData("1 stk paprika", "1|stk|paprika|")]
    [InlineData("1 klype salt", "1|klype|salt|")]
    [InlineData("en klype salt", "1|klype|salt|")]
    [InlineData("1 løk (finhakket)", "1|stk*|løk|finhakket")]
    [InlineData("2 mellomstore løk", "2|stk*|mellomstore løk|")]
    [InlineData("2,5 dl melk", "2.5|dl|melk|")]
    [InlineData("1 kg poteter", "1|kg|poteter|")]
    [InlineData("100 gram sukker", "100|g|sukker|")]
    [InlineData("1 liter vann", "1|l|vann|")]
    [InlineData("2 spiseskjeer olje", "2|ss|olje|")]
    [InlineData("3 teskjeer sukker", "3|ts|sukker|")]
    [InlineData("1 ss smør", "1|ss|smør|")]
    [InlineData("2 dl. melk", "2|dl|melk|")]
    [InlineData("3 kopper mel", "3|kopp|mel|")]
    [InlineData("2 til 3 ss olje", "2..3|ss|olje|2-3")]
    [InlineData("1–2 dl fløte", "1..2|dl|fløte|1-2")]
    [InlineData("3-2 dl melk", "2..3|dl|melk|2-3")]
    [InlineData("en halv løk", "0.5|stk*|løk|")]
    [InlineData("1 pk tortilla", "1|?pk|tortilla|")]
    [InlineData("1 pose spinat", "1|?pose|spinat|")]
    [InlineData("1 boks (400 g) hakkede tomater", "1|?boks|hakkede tomater|400 g")]
    [InlineData("10 g fersk ingefær", "10|g|fersk ingefær|")]
    [InlineData("ca. 200 g ost", "200|g|ost|")]
    [InlineData("200 g Smør (romtemperert), i terninger", "200|g|Smør|romtemperert; i terninger")]
    // English
    [InlineData("1½ cup flour", "1.5|kopp|flour|")]
    [InlineData("1 tbsp olive oil", "1|ss|olive oil|")]
    [InlineData("2 tsp. salt", "2|ts|salt|")]
    [InlineData("2 Tbsp Butter", "2|ss|Butter|")]
    [InlineData("a pinch of salt", "1|klype|salt|")]
    [InlineData("1 cup of flour", "1|kopp|flour|")]
    [InlineData("4 cloves garlic, minced", "4|fedd|garlic|minced")]
    [InlineData("1 oz cheese", "1|?oz|cheese|")]
    [InlineData("2 lb potatoes", "2|?lb|potatoes|")]
    [InlineData("1 to 2 tsp salt", "1..2|ts|salt|1-2")]
    [InlineData("half a cup of sugar", "0.5|kopp|sugar|")]
    [InlineData("an egg", "1|stk*|egg|")]
    [InlineData("3/4 cup sugar", "0.75|kopp|sugar|")]
    [InlineData("2 1/2 cups milk", "2.5|kopp|milk|")]
    [InlineData("2.5 dl milk", "2.5|dl|milk|")]
    [InlineData("1 lb ground beef", "1|?lb|ground beef|")]
    [InlineData("a little salt", "-|-|a little salt|")]
    // Formats: attached units, unicode, bullets, odd spaces
    [InlineData("500ml vann", "500|ml|vann|")]
    [InlineData("200g mel", "200|g|mel|")]
    [InlineData("1.5kg ris", "1.5|kg|ris|")]
    [InlineData("1⁄2 ts salt", "0.5|ts|salt|")]
    [InlineData("¾ dl sukker", "0.75|dl|sukker|")]
    [InlineData("⅓ cup oil", "0.3333|kopp|oil|")]
    [InlineData("2½ dl vann", "2.5|dl|vann|")]
    [InlineData("- 2 dl melk", "2|dl|melk|")]
    [InlineData("• 3 egg", "3|stk*|egg|")]
    [InlineData("  2 dl   melk  ", "2|dl|melk|")]
    // No quantity: "to taste"
    [InlineData("salt", "-|-|salt|")]
    [InlineData("pepper, malt", "-|-|pepper|malt")]
    [InlineData("Smør til steking", "-|-|Smør|til steking")]
    [InlineData("salt etter smak", "-|-|salt|etter smak")]
    [InlineData("olive oil, to taste", "-|-|olive oil|to taste")]
    public void Parses_a_line_into_quantity_unit_ingredient_and_note(string line, string expected) =>
        Assert.Equal(expected, Show(IngredientLineParser.Parse(line)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("200")]
    [InlineData("2 dl")]
    [InlineData("2-3 stk")]
    [InlineData("0 g salt")]
    [InlineData("100000000 g salt")]
    [InlineData("1/0 dl melk")]
    [InlineData("---")]
    [InlineData("12 345")]
    public void Garbage_is_unparseable_and_keeps_the_original(string? line)
    {
        var parsed = IngredientLineParser.Parse(line);

        Assert.Equal(ParsedLineKind.Unparseable, parsed.Kind);
        Assert.Equal(line ?? string.Empty, parsed.Original);
        Assert.Null(parsed.IngredientText);
        Assert.Null(parsed.Quantity);
    }

    [Theory]
    [InlineData("Til sausen:")]
    [InlineData("Sauce:")]
    [InlineData("INGREDIENTS FOR THE DOUGH:")]
    public void Section_labels_are_headings(string line) =>
        Assert.Equal(ParsedLineKind.Heading, IngredientLineParser.Parse(line).Kind);

    [Fact]
    public void A_range_keeps_the_upper_bound_as_the_quantity_and_the_lower_bound_separately()
    {
        var parsed = IngredientLineParser.Parse("2-3 fedd hvitløk");

        Assert.Equal(3m, parsed.Quantity);
        Assert.Equal(2m, parsed.QuantityLow);
        Assert.Equal("fedd", parsed.UnitCode);
    }

    [Fact]
    public void An_equal_range_is_just_the_quantity()
    {
        var parsed = IngredientLineParser.Parse("2-2 dl melk");

        Assert.Equal(2m, parsed.Quantity);
        Assert.Null(parsed.QuantityLow);
    }

    [Fact]
    public void The_original_text_is_kept_untouched()
    {
        var parsed = IngredientLineParser.Parse("  1½ cup flour ");

        Assert.Equal("  1½ cup flour ", parsed.Original);
    }

    [Fact]
    public void A_unit_word_the_catalog_lacks_has_no_unit_code()
    {
        var parsed = IngredientLineParser.Parse("1 boks tomater");

        Assert.Null(parsed.UnitCode);
        Assert.Equal("boks", parsed.UnitText);
        Assert.Equal(1m, parsed.Quantity);
    }

    [Fact]
    public void Every_unit_code_produced_exists_in_the_seed()
    {
        var seeded = Foodplanner.Domain.Catalog.UnitSeed.All.Select(u => u.Code).ToHashSet();
        foreach (var word in new[] { "g", "kg", "ml", "dl", "l", "ts", "ss", "kopp", "stk", "fedd", "klype", "tbsp", "tsp", "cup", "gram", "teskje", "spiseskje", "pinch", "clove" })
        {
            Assert.Contains(UnitWords.CodeFor(word)!, seeded);
        }
    }

    [Fact]
    public void ParseAll_splits_salt_and_pepper_into_two_lines_and_drops_headings()
    {
        var lines = IngredientLineParser.ParseAll(["Til sausen:", "2 dl fløte", "salt og pepper", "Salt & pepper", "salt and freshly ground black pepper"]);

        Assert.Equal(
            ["2 dl fløte", "salt", "pepper", "Salt", "pepper", "salt", "freshly ground black pepper"],
            lines.Select(l => l.Quantity is null ? l.IngredientText! : $"{Num(l.Quantity.Value)} {l.UnitCode} {l.IngredientText}"));
        Assert.All(lines, l => Assert.NotNull(l.Original));
    }

    [Fact]
    public void ParseAll_does_not_split_lines_with_an_amount_or_long_parts()
    {
        var lines = IngredientLineParser.ParseAll(["2 dl melk og vann", "salt og pepper og litt mer av hvitløk og enda mer her"]);

        Assert.Equal(2, lines.Count);
        Assert.Equal("melk og vann", lines[0].IngredientText);
    }

    [Fact]
    public void ParseAll_keeps_unparseable_lines_in_order()
    {
        var lines = IngredientLineParser.ParseAll(["2 dl melk", "???", "3 egg"]);

        Assert.Equal([ParsedLineKind.Parsed, ParsedLineKind.Unparseable, ParsedLineKind.Parsed], lines.Select(l => l.Kind));
    }

    [Fact]
    public void A_very_long_note_is_capped_at_the_recipe_line_limit()
    {
        var parsed = IngredientLineParser.Parse("1 løk, " + new string('x', 500));

        Assert.True(parsed.Note!.Length <= Foodplanner.Domain.Recipes.RecipeIngredient.MaxNoteLength);
    }
}
