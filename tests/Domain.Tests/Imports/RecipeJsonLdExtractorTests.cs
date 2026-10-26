using Foodplanner.Domain.Imports;

namespace Foodplanner.Domain.Tests.Imports;

public class RecipeJsonLdExtractorTests
{
    [Fact]
    public void A_simple_norwegian_recipe_is_extracted()
    {
        var found = RecipeJsonLdExtractor.Extract(JsonLdFixtures.NorwegianSimple);

        Assert.NotNull(found);
        var r = found.Recipe;
        Assert.Equal("Perfekte pannekaker", r.Title);
        Assert.Equal("Tynne og gode & enkle.", r.Description);
        Assert.Equal(4, r.Servings);
        Assert.Equal(10, r.PrepMinutes);
        Assert.Equal(20, r.CookMinutes);
        Assert.Equal("https://img.example.test/pannekaker-16x9.jpg", r.ImageUrl);
        Assert.Equal(["2,5 dl hvetemel", "5 dl melk", "3 egg", "½ ts salt", "smør til steking"], r.IngredientLines);
        Assert.Equal(["Visp sammen mel og melk.", "Ha i eggene og saltet.", "Stek tynne pannekaker i smør."], r.Steps);
        Assert.Contains("\"Recipe\"", found.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_graph_with_sections_flattens_steps_in_order()
    {
        var r = RecipeJsonLdExtractor.Extract(JsonLdFixtures.GraphWithSections)!.Recipe;

        Assert.Equal("Lasagne", r.Title);
        Assert.Equal(6, r.Servings); // ["6", "6 servings"]
        Assert.Equal(25, r.PrepMinutes);
        Assert.Equal(60, r.CookMinutes);
        Assert.Equal("https://img.example.test/lasagne.jpg", r.ImageUrl); // ImageObject
        Assert.Equal(7, r.IngredientLines.Count);
        Assert.Equal(
            ["Brun kjøttdeigen i en panne.", "Ha i løk, hvitløk og tomater. Kok i 20 minutter.", "Legg lag på lag i en form.", "Stek i ovnen ved 200 grader i 35 minutter."],
            r.Steps);
    }

    [Fact]
    public void A_root_array_with_a_type_array_and_html_instructions_is_extracted()
    {
        var r = RecipeJsonLdExtractor.Extract(JsonLdFixtures.ArrayWithHtmlInstructions)!.Recipe;

        Assert.Equal("Chili sin carne", r.Title);
        Assert.Equal(4, r.Servings);
        Assert.Null(r.PrepMinutes);
        Assert.Equal(45, r.CookMinutes); // only a total is given
        Assert.Equal(["1 løk", "2 ss olje", "1 boks kidneybønner"], r.IngredientLines); // one string with line breaks
        Assert.Equal(["Fres løken i oljen.", "Ha i bønner og & krydder.", "Server varm."], r.Steps);
        Assert.Equal("https://img.example.test/chili.png", r.ImageUrl);
    }

    [Fact]
    public void A_second_block_with_comment_markers_trailing_commas_and_raw_line_breaks_is_repaired()
    {
        var r = RecipeJsonLdExtractor.Extract(JsonLdFixtures.SloppyBlocks)!.Recipe;

        Assert.Equal("Suppe med brød", r.Title);
        Assert.Equal(2, r.Servings); // "Serves 2-3": the first number
        Assert.Equal(["1 l vann", "2 buljongterninger"], r.IngredientLines);
        Assert.Equal(["Kok opp vannet.", "Ha i terningene."], r.Steps);
    }

    [Fact]
    public void A_recipe_nested_under_main_entity_is_found_and_a_broken_duration_is_ignored()
    {
        var r = RecipeJsonLdExtractor.Extract(JsonLdFixtures.NestedInWebPage)!.Recipe;

        Assert.Equal("Rømmegrøt", r.Title);
        Assert.Equal(5, r.Servings);
        Assert.Null(r.CookMinutes);
        Assert.Equal(2, r.Steps.Count);
    }

    [Fact]
    public void A_json_document_response_without_html_is_extracted()
    {
        var r = RecipeJsonLdExtractor.Extract(JsonLdFixtures.PlainDocument)!.Recipe;

        Assert.Equal("Vafler", r.Title);
        Assert.Equal(8, r.Servings);
        Assert.Equal(["3 dl mel", "2 egg"], r.IngredientLines);
    }

    [Theory]
    [InlineData(JsonLdFixtures.ArticleOnly)]
    [InlineData(JsonLdFixtures.BrokenJson)]
    [InlineData(JsonLdFixtures.NoJsonLd)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("<script type=\"application/ld+json\"></script>")]
    [InlineData("{\"@type\":\"Recipe\"")]
    public void Pages_without_a_readable_recipe_give_null(string? content) =>
        Assert.Null(RecipeJsonLdExtractor.Extract(content));

    [Fact]
    public void A_recipe_without_ingredients_or_steps_warns()
    {
        var r = RecipeJsonLdExtractor.Extract("""<script type="application/ld+json">{"@type":"Recipe","name":"Tom"}</script>""")!.Recipe;

        Assert.Equal(2, r.Warnings.Count);
        Assert.Empty(r.IngredientLines);
        Assert.Empty(r.Steps);
    }

    [Theory]
    [InlineData("\"recipeYield\": 0", null)]
    [InlineData("\"recipeYield\": 1000", null)]
    [InlineData("\"recipeYield\": \"12 muffins\"", 12)]
    [InlineData("\"recipeYield\": [\"abc\", \"3\"]", 3)]
    [InlineData("\"recipeYield\": {\"x\": 1}", null)]
    [InlineData("\"recipeYield\": \"many\"", null)]
    public void Yield_becomes_servings_only_when_it_is_a_sensible_number(string yieldJson, int? expected)
    {
        var json = $$"""<script type="application/ld+json">{"@type":"Recipe","name":"X",{{yieldJson}} }</script>""";

        Assert.Equal(expected, RecipeJsonLdExtractor.Extract(json)!.Recipe.Servings);
    }

    [Theory]
    [InlineData("\"image\": \"javascript:alert(1)\"")]
    [InlineData("\"image\": \"/relative/path.jpg\"")]
    [InlineData("\"image\": \"ftp://x.test/a.jpg\"")]
    [InlineData("\"image\": 42")]
    public void Only_absolute_http_image_urls_are_kept_as_candidates(string image)
    {
        var json = $$"""<script type="application/ld+json">{"@type":"Recipe","name":"X",{{image}} }</script>""";

        Assert.Null(RecipeJsonLdExtractor.Extract(json)!.Recipe.ImageUrl);
    }

    [Fact]
    public void Html_in_text_fields_is_removed_and_the_title_is_capped()
    {
        var json = $$"""<script type="application/ld+json">{"@type":"Recipe","name":"<b>Fin</b> suppe {{new string('x', 400)}}"}</script>""";

        var title = RecipeJsonLdExtractor.Extract(json)!.Recipe.Title!;

        Assert.StartsWith("Fin suppe", title, StringComparison.Ordinal);
        Assert.True(title.Length <= Foodplanner.Domain.Recipes.Recipe.MaxTitleLength);
        Assert.DoesNotContain('<', title);
    }

    [Fact]
    public void Deeply_nested_json_does_not_overflow_the_stack()
    {
        var json = "<script type=\"application/ld+json\">" + string.Concat(Enumerable.Repeat("[", 60)) + string.Concat(Enumerable.Repeat("]", 60)) + "</script>";

        Assert.Null(RecipeJsonLdExtractor.Extract(json));
    }
}
