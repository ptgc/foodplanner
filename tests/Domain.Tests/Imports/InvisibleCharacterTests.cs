using Foodplanner.Domain.Imports;

namespace Foodplanner.Domain.Tests.Imports;

/// <summary>The source uses code points, not literal invisible characters; these fail if a code point is wrong.</summary>
public class InvisibleCharacterTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();
    private static readonly string Bom = ((char)0xFEFF).ToString();

    [Fact]
    public void A_no_break_space_between_amount_and_unit_is_an_ordinary_space()
    {
        var parsed = IngredientLineParser.Parse("2" + Nbsp + "dl" + Nbsp + "melk");

        Assert.Equal(2m, parsed.Quantity);
        Assert.Equal("dl", parsed.UnitCode);
        Assert.Equal("melk", parsed.IngredientText);
    }

    [Theory]
    [InlineData(0x2009)]
    [InlineData(0x202F)]
    public void Thin_spaces_are_ordinary_spaces(int codePoint)
    {
        var parsed = IngredientLineParser.Parse("2" + (char)codePoint + "dl melk");

        Assert.Equal("dl", parsed.UnitCode);
    }

    [Fact]
    public void The_fraction_slash_and_dash_variants_are_understood()
    {
        var half = IngredientLineParser.Parse("1" + (char)0x2044 + "2 ts salt");
        Assert.Equal(0.5m, half.Quantity);

        foreach (var dash in new[] { 0x2013, 0x2014, 0x2212 })
        {
            var range = IngredientLineParser.Parse("2" + (char)dash + "3 dl melk");
            Assert.Equal(3m, range.Quantity);
            Assert.Equal(2m, range.QuantityLow);
        }
    }

    [Fact]
    public void A_no_break_space_in_a_step_becomes_a_space()
    {
        Assert.Equal("Bland alt godt", StepTextCleaner.Clean("Bland" + Nbsp + "alt" + Nbsp + "godt"));
        Assert.Equal("Stek 5 min", StepTextCleaner.Clean("Stek" + Nbsp + "5 min"));
    }

    [Fact]
    public void A_step_number_followed_by_an_en_dash_is_removed()
    {
        Assert.Equal("Server", StepTextCleaner.Clean("Steg 3 " + (char)0x2013 + " Server"));
    }

    [Fact]
    public void Json_ld_with_a_byte_order_mark_in_front_is_read()
    {
        var json = Bom + "{\"@type\":\"Recipe\",\"name\":\"Vafler\",\"recipeIngredient\":[\"2 egg\"]}";

        Assert.Equal("Vafler", RecipeJsonLdExtractor.Extract(json)!.Recipe.Title);
        Assert.Equal("Vafler", RecipeJsonLdExtractor.Extract("  \r\n" + json)!.Recipe.Title);
    }
}
