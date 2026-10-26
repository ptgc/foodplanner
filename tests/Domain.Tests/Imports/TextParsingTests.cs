using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Imports;

public class StepTextCleanerTests
{
    [Theory]
    [InlineData("1. Bland alt", "Bland alt")]
    [InlineData("2) Stek i 5 min", "Stek i 5 min")]
    [InlineData("Steg 3: Server", "Server")]
    [InlineData("Step 4 - Cool", "Cool")]
    [InlineData("- Rør godt", "Rør godt")]
    [InlineData("• Rør godt", "Rør godt")]
    [InlineData("  Bland   alt \t godt  ", "Bland alt godt")]
    [InlineData("Bland <b>alt</b> &amp; litt&nbsp;mer", "Bland alt & litt mer")]
    [InlineData("<p>Hei</p><script>alert(1)</script>", "Hei")]
    [InlineData("Ha i 2 dl melk", "Ha i 2 dl melk")]
    [InlineData("Bruk 1.5 dl melk", "Bruk 1.5 dl melk")]
    [InlineData("Line one\r\nline two", "Line one line two")]
    [InlineData("", "")]
    [InlineData("<br/>", "")]
    public void Clean_removes_markup_markers_and_extra_space(string input, string expected) =>
        Assert.Equal(expected, StepTextCleaner.Clean(input));

    [Fact]
    public void Clean_handles_null_and_caps_the_length()
    {
        Assert.Equal(string.Empty, StepTextCleaner.Clean(null));
        Assert.Equal(10, StepTextCleaner.Clean(new string('x', 50), 10).Length);
    }

    [Fact]
    public void Clean_drops_control_characters()
    {
        Assert.Equal("Bland alt", StepTextCleaner.Clean("Bland\u0001 alt\u0000"));
    }

    [Fact]
    public void Split_makes_one_step_per_line_or_block_and_drops_empty_ones()
    {
        var steps = StepTextCleaner.Split("<ol><li>1. Fres løk.</li><li></li><li>Ha i bønner.</li></ol>\n\n3. Server");

        Assert.Equal(["Fres løk.", "Ha i bønner.", "Server"], steps);
    }
}

public class DurationsTests
{
    [Theory]
    [InlineData("PT30M", 30)]
    [InlineData("PT1H", 60)]
    [InlineData("PT1H30M", 90)]
    [InlineData("pt1h30m", 90)]
    [InlineData("P0DT45M", 45)]
    [InlineData("P1DT2H", 1560)]
    [InlineData("P1W", 10080)]
    [InlineData("PT90M", 90)]
    [InlineData("PT0H25M", 25)]
    [InlineData("PT1.5H", 90)]
    [InlineData("PT30S", 1)]
    [InlineData("PT5S", 0, true)]
    [InlineData("PT0M", 0, true)]
    [InlineData("P", 0, true)]
    [InlineData("PT", 0, true)]
    [InlineData("30 minutes", 0, true)]
    [InlineData("", 0, true)]
    [InlineData(null, 0, true)]
    public void FromIso_parses_schema_org_durations(string? value, int expected, bool isNull = false) =>
        Assert.Equal(isNull ? null : expected, Durations.FromIso(value));

    [Theory]
    [InlineData("30 min", 30)]
    [InlineData("45 minutter", 45)]
    [InlineData("1 time 15 min", 75)]
    [InlineData("1 time", 60)]
    [InlineData("2 timer", 120)]
    [InlineData("1h 30m", 90)]
    [InlineData("1 hour 5 minutes", 65)]
    [InlineData("1,5 timer", 90)]
    [InlineData("ca. 20 min", 20)]
    public void FromText_parses_plain_text(string value, int expected) =>
        Assert.Equal(expected, Durations.FromText(value));

    [Theory]
    [InlineData("snart")]
    [InlineData("")]
    [InlineData(null)]
    public void FromText_gives_null_when_nothing_is_found(string? value) =>
        Assert.Null(Durations.FromText(value));
}

public class PastedRecipeParserTests
{
    private const string WithHeadings = """
        Mormors pannekaker

        Porsjoner: 4
        Forberedelse: 10 min
        Koketid: 1 time 15 min

        Ingredienser
        2,5 dl hvetemel
        5 dl melk
        3 egg
        ½ ts salt
        Smør til steking

        Fremgangsmåte
        1. Visp sammen mel og melk.
        2. Ha i eggene og saltet.
        3. Stek tynne pannekaker i smør.
        """;

    [Fact]
    public void Text_with_headings_gives_title_meta_ingredients_and_steps()
    {
        var r = PastedRecipeParser.Parse(WithHeadings);

        Assert.Equal("Mormors pannekaker", r.Title);
        Assert.Equal(4, r.Servings);
        Assert.Equal(10, r.PrepMinutes);
        Assert.Equal(75, r.CookMinutes);
        Assert.Equal(["2,5 dl hvetemel", "5 dl melk", "3 egg", "½ ts salt", "Smør til steking"], r.IngredientLines);
        Assert.Equal(["Visp sammen mel og melk.", "Ha i eggene og saltet.", "Stek tynne pannekaker i smør."], r.Steps);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void English_headings_markdown_and_serves_are_understood()
    {
        const string text = """
            # Pancakes
            Serves 6
            Prep time: 5 minutes
            Cook time: 20 minutes

            ## Ingredients:
            - 2 cups flour
            - 1½ cups milk
            - 2 eggs

            ## Instructions:
            Whisk everything.
            Fry in butter.
            """;

        var r = PastedRecipeParser.Parse(text);

        Assert.Equal("Pancakes", r.Title);
        Assert.Equal(6, r.Servings);
        Assert.Equal(5, r.PrepMinutes);
        Assert.Equal(20, r.CookMinutes);
        Assert.Equal(["- 2 cups flour", "- 1½ cups milk", "- 2 eggs"], r.IngredientLines); // the line parser removes the bullets
        Assert.Equal(["Whisk everything.", "Fry in butter."], r.Steps);
    }

    [Fact]
    public void Text_without_headings_splits_ingredients_from_steps_by_how_the_lines_look()
    {
        const string text = """
            Enkel omelett
            4 porsjoner
            3 egg
            1 dl melk
            salt
            Pisk eggene sammen med melken og saltet.
            Stek i en varm panne til omeletten er gyllen.
            """;

        var r = PastedRecipeParser.Parse(text);

        Assert.Equal("Enkel omelett", r.Title);
        Assert.Equal(4, r.Servings);
        Assert.Equal(["3 egg", "1 dl melk", "salt"], r.IngredientLines);
        Assert.Equal(2, r.Steps.Count);
    }

    [Fact]
    public void Numbered_short_lines_are_steps_not_ingredients()
    {
        const string text = """
            Eggerøre
            4 egg
            1. Pisk
            2. Stek
            """;

        var r = PastedRecipeParser.Parse(text);

        Assert.Equal(["4 egg"], r.IngredientLines);
        Assert.Equal(["Pisk", "Stek"], r.Steps);
    }

    [Fact]
    public void Intro_text_before_the_first_heading_becomes_the_description()
    {
        const string text = "Suppe\nEn varm og god suppe til høsten.\nIngredienser\n1 l vann\nFremgangsmåte\nKok.";

        var r = PastedRecipeParser.Parse(text);

        Assert.Equal("Suppe", r.Title);
        Assert.Equal("En varm og god suppe til høsten.", r.Description);
    }

    [Theory]
    [InlineData("Til 4 personer", 4)]
    [InlineData("4 porsjoner", 4)]
    [InlineData("Gir 12 stykker", 12)]
    [InlineData("Servings: 8", 8)]
    [InlineData("Yield: 2-3 servings", 3)]
    [InlineData("Antall porsjoner: 6", 6)]
    public void Servings_are_found_in_several_phrasings(string line, int expected)
    {
        var r = PastedRecipeParser.Parse($"Tittel\n{line}\nIngredienser\n1 egg\nFremgangsmåte\nKok");

        Assert.Equal(expected, r.Servings);
    }

    [Fact]
    public void Garbage_and_empty_text_give_warnings_not_exceptions()
    {
        Assert.Equal(2, PastedRecipeParser.Parse("").Warnings.Count);
        Assert.Equal(2, PastedRecipeParser.Parse(null).Warnings.Count);
        Assert.Null(PastedRecipeParser.Parse("   \n \n").Title);
        Assert.Equal("Bare en tittel", PastedRecipeParser.Parse("Bare en tittel").Title);
    }

    [Fact]
    public void A_very_long_title_line_is_capped()
    {
        var r = PastedRecipeParser.Parse(new string('a', 500) + "\n1 egg");

        Assert.True(r.Title!.Length <= Recipe.MaxTitleLength);
    }

    [Fact]
    public void Windows_line_endings_work()
    {
        var r = PastedRecipeParser.Parse("Suppe\r\nIngredienser\r\n1 l vann\r\nFremgangsmåte\r\nKok opp.\r\n");

        Assert.Equal(["1 l vann"], r.IngredientLines);
        Assert.Equal(["Kok opp."], r.Steps);
    }
}
