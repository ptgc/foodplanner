using System.Globalization;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Imports;

/// <summary>
/// T-076: amount kind, purpose, optional and alternatives, read from real Norwegian and English recipe lines (many are from
/// matprat.no, where "salt og pepper" and "margarin til steking" went wrong before).
/// </summary>
public class IngredientLineKindTests
{
    /// <summary>"kind|purpose|optional|quantity unit|ingredient|alternatives (/)|note".</summary>
    private static string Show(ParsedIngredientLine p)
    {
        Assert.Equal(ParsedLineKind.Parsed, p.Kind);
        var amount = p.Quantity is null ? "-" : p.Quantity.Value.ToString("0.####", CultureInfo.InvariantCulture) + " " + (p.UnitText ?? p.UnitCode);
        var opt = p.IsOptional ? "opt" : "-";
        return $"{p.AmountKind}|{p.Purpose}|{opt}|{amount}|{p.IngredientText}|{string.Join('/', p.Alternatives.Select(a => a.Text))}|{p.Note}";
    }

    [Theory]
    // The matprat.no lines that went wrong
    [InlineData("2 ss margarin eller olje til steking", "Measured|Frying|-|2 ss|margarin|olje|")]
    [InlineData("3 ss margarin til steking", "Measured|Frying|-|3 ss|margarin||")]
    [InlineData("0,5 ts pepper", "Measured|Main|-|0.5 ts|pepper||")]
    [InlineData("1 dl revet hvitost (kan sløyfes)", "Measured|Main|opt|1 dl|revet hvitost||")]
    [InlineData("ca. 400 g torskefilet eller annen kokt, hvit fisk", "Measured|Main|-|400 g|torskefilet|annen kokt hvit fisk|")]
    [InlineData("5 ss revet parmesan eller annen hvit ost", "Measured|Main|-|5 ss|revet parmesan|annen hvit ost|")]
    [InlineData("0,5 potte frisk basilikum , evt. litt tørket basilikum", "Measured|Main|-|0.5 potte|frisk basilikum|litt tørket basilikum|")]
    [InlineData("2 bokser hermetiske tomater (á 400 g)", "Measured|Main|-|2 bokser|hermetiske tomater||á 400 g")]
    // To taste
    [InlineData("salt", "ToTaste|Seasoning|-|-|salt||")]
    [InlineData("salt etter smak", "ToTaste|Seasoning|-|-|salt||")]
    [InlineData("pepper, etter smak", "ToTaste|Seasoning|-|-|pepper||")]
    [InlineData("Salt og pepper etter smak", "ToTaste|Seasoning|-|-|Salt og pepper||")]
    [InlineData("black pepper, to taste", "ToTaste|Seasoning|-|-|black pepper||")]
    [InlineData("salt and pepper to taste", "ToTaste|Seasoning|-|-|salt and pepper||")]
    [InlineData("a little salt", "ToTaste|Seasoning|-|-|a little salt||")]
    [InlineData("1 ts salt, etter smak", "Measured|Seasoning|-|1 ts|salt||")]
    // Frying and as needed
    [InlineData("smør til steking", "AsNeeded|Frying|-|-|smør||")]
    [InlineData("Olje til stekning", "AsNeeded|Frying|-|-|Olje||")]
    [InlineData("margarin eller smør til steking", "AsNeeded|Frying|-|-|margarin|smør|")]
    [InlineData("olje å steke i", "AsNeeded|Frying|-|-|olje||")]
    [InlineData("2 ss smør til steking", "Measured|Frying|-|2 ss|smør||")]
    [InlineData("butter for frying", "AsNeeded|Frying|-|-|butter||")]
    [InlineData("2 tbsp oil for frying", "Measured|Frying|-|2 ss|oil||")]
    [InlineData("olje til pensling", "AsNeeded|Main|-|-|olje||til pensling")]
    [InlineData("vegetable oil, as needed", "AsNeeded|Main|-|-|vegetable oil||")]
    // Serving and garnish
    [InlineData("rømme til servering", "AsNeeded|Serving|-|-|rømme||")]
    [InlineData("2 dl rømme til servering", "Measured|Serving|-|2 dl|rømme||")]
    [InlineData("sour cream, to serve", "AsNeeded|Serving|-|-|sour cream||")]
    [InlineData("persille til pynt", "AsNeeded|Garnish|-|-|persille||")]
    [InlineData("1 neve persille, til pynt", "Measured|Garnish|-|1 neve|persille||")]
    [InlineData("fresh parsley, to garnish", "AsNeeded|Garnish|-|-|fresh parsley||")]
    // Optional
    [InlineData("evt. 1 ts chili", "Measured|Main|opt|1 ts|chili||")]
    [InlineData("1 ts chili, kan sløyfes", "Measured|Main|opt|1 ts|chili||")]
    [InlineData("1 ts chili (valgfritt)", "Measured|Main|opt|1 ts|chili||")]
    [InlineData("1 tsp chili flakes (optional)", "Measured|Main|opt|1 ts|chili flakes||")]
    [InlineData("2 ss kapers, eventuelt", "Measured|Main|opt|2 ss|kapers||")]
    // Alternatives
    [InlineData("2 dl melk eller fløte", "Measured|Main|-|2 dl|melk|fløte|")]
    [InlineData("200 g smør eller margarin", "Measured|Main|-|200 g|smør|margarin|")]
    [InlineData("1 dl olje eller smør eller margarin", "Measured|Main|-|1 dl|olje|smør/margarin|")]
    [InlineData("1 cup milk or cream", "Measured|Main|-|1 kopp|milk|cream|")]
    [InlineData("3 ss smør, eller margarin", "Measured|Main|-|3 ss|smør|margarin|")]
    [InlineData("2 dl melk eller 1 dl fløte", "Measured|Main|-|2 dl|melk eller 1 dl fløte||")]
    [InlineData("1 stk rød eller gul paprika", "Measured|Main|-|1 stk|rød|gul paprika|")]
    public void Reads_kind_purpose_optional_and_alternatives(string line, string expected) =>
        Assert.Equal(expected, Show(IngredientLineParser.Parse(line)));

    [Fact]
    public void Salt_og_pepper_is_two_to_taste_lines()
    {
        var lines = IngredientLineParser.ParseAll(["salt og pepper"]);

        Assert.Equal(
            ["ToTaste|Seasoning|-|-|salt||", "ToTaste|Seasoning|-|-|pepper||"],
            lines.Select(Show));
    }

    [Fact]
    public void Salt_og_pepper_etter_smak_is_split_too()
    {
        var lines = IngredientLineParser.ParseAll(["salt og pepper etter smak"]);

        Assert.Equal(["salt", "pepper"], lines.Select(l => l.IngredientText));
        Assert.All(lines, l => Assert.Equal(AmountKind.ToTaste, l.AmountKind));
    }

    [Fact]
    public void A_split_line_keeps_the_whole_text_for_a_second_matching_try()
    {
        var line = IngredientLineParser.Parse("1 stk rød eller gul paprika");

        Assert.Equal("rød eller gul paprika", line.UnsplitText);
    }

    [Fact]
    public void Unmatched_alternatives_and_the_remaining_phrase_are_kept_in_the_note()
    {
        var line = IngredientLineParser.Parse("0,5 potte frisk basilikum , evt. litt tørket basilikum");

        Assert.Equal("evt. litt tørket basilikum", Assert.Single(line.Alternatives).NoteText);
    }

    [Fact]
    public void Measured_lines_never_lose_their_amount_to_a_phrase()
    {
        var line = IngredientLineParser.Parse("2 dl melk, etter smak");

        Assert.Equal(AmountKind.Measured, line.AmountKind);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("dl", line.UnitCode);
    }
}

/// <summary>The draft builder matches alternatives to the catalog and turns the rest into note text.</summary>
public class ImportDraftKindTests
{
    private static readonly IngredientMatcher Matcher = new(IngredientSeed.All.Select(r => new IngredientCandidate(r.Id, r.Nb, [r.Nb, r.Name], false)));

    private static Guid? UnitId(string code) => UnitSeed.All.SingleOrDefault(u => u.Code == code)?.Id;

    private static Guid Id(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    private static ImportDraftLine Line(string text) =>
        ImportDraftBuilder.Build(new ImportedRecipe("T", null, 4, null, null, null, [text], [], []), Matcher, UnitId).Lines.Single();

    [Fact]
    public void A_catalog_alternative_becomes_an_id()
    {
        var line = Line("2 dl melk eller fløte");

        Assert.Equal(Id("melk"), line.IngredientId);
        Assert.Equal([Id("floete")], line.Alternatives);
        Assert.Null(line.Note);
        Assert.Equal(ImportLineStatus.Matched, line.Status);
    }

    [Fact]
    public void An_unmatched_alternative_goes_into_the_note_not_into_a_new_ingredient()
    {
        var line = Line("200 g smør eller dragefrukt");

        Assert.Equal(Id("smor"), line.IngredientId);
        Assert.Empty(line.Alternatives);
        Assert.Equal("eller dragefrukt", line.Note);
    }

    [Theory]
    [InlineData("200 g smør eller margarin", "smor", "margarin")]
    [InlineData("1 dl olje eller smør", "olje", "smor")]
    [InlineData("2 ss margarin eller olje til steking", "margarin", "olje")]
    [InlineData("3 ss smør eller nøytral olje", "smor", "noytral_olje")]
    public void Margarine_and_oil_are_matched_as_main_ingredient_and_as_alternative(string text, string main, string alternative)
    {
        var line = Line(text);

        Assert.Equal(Id(main), line.IngredientId);
        Assert.Equal([Id(alternative)], line.Alternatives);
        Assert.Null(line.Note);
    }

    [Theory]
    [InlineData("margarin til steking", "margarin")]
    [InlineData("olje til steking", "olje")]
    [InlineData("2 ss olje", "olje")]
    [InlineData("nøytral olje", "noytral_olje")]
    [InlineData("1 ss oil", "olje")]
    public void Margarine_and_oil_lines_match_the_new_catalog_rows(string text, string key)
    {
        var line = Line(text);

        Assert.Equal(Id(key), line.IngredientId);
        Assert.Equal(ImportLineStatus.Matched, line.Status);
    }

    [Fact]
    public void A_generic_alternative_is_matched_by_its_last_words()
    {
        var line = Line("5 ss revet parmesan eller annen hvit ost");

        Assert.Equal(Id("parmesan"), line.IngredientId);
        Assert.Equal([Id("ost")], line.Alternatives);
    }

    [Fact]
    public void An_alternative_that_is_the_same_ingredient_stays_in_the_note()
    {
        var line = Line("0,5 potte frisk basilikum , evt. litt tørket basilikum");

        Assert.Equal(Id("basilikum"), line.IngredientId);
        Assert.Empty(line.Alternatives);
        Assert.Equal("evt. litt tørket basilikum", line.Note);
    }

    [Fact]
    public void Salt_etter_smak_is_matched_to_taste_with_seasoning_purpose()
    {
        var line = Line("salt etter smak");

        Assert.Equal(AmountKind.ToTaste, line.AmountKind);
        Assert.Equal(IngredientPurpose.Seasoning, line.Purpose);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitId);
        Assert.Equal(ImportLineStatus.Matched, line.Status);
    }

    [Fact]
    public void Frying_fat_without_an_amount_is_as_needed()
    {
        var line = Line("smør til steking");

        Assert.Equal(AmountKind.AsNeeded, line.AmountKind);
        Assert.Equal(IngredientPurpose.Frying, line.Purpose);
        Assert.Equal(ImportLineStatus.Matched, line.Status);
    }

    [Fact]
    public void Optional_is_carried_to_the_draft()
    {
        var line = Line("1 dl revet ost (kan sløyfes)");

        Assert.True(line.IsOptional);
        Assert.Equal(AmountKind.Measured, line.AmountKind);
        Assert.Equal(Id("ost"), line.IngredientId);
    }

    [Fact]
    public void The_whole_text_is_tried_when_the_first_part_of_a_split_does_not_match()
    {
        var line = Line("1 stk rød eller gul paprika");

        Assert.Equal(Id("paprika"), line.IngredientId);
        Assert.Empty(line.Alternatives);
    }

    [Fact]
    public void A_draft_saved_before_amount_kinds_reads_to_taste_lines_as_ToTaste()
    {
        const string legacy = """
            {"title":"T","description":null,"servings":4,"prepMinutes":null,"cookMinutes":null,"imageUrl":null,"steps":[],
            "lines":[{"original":"salt","ingredientText":"salt","quantity":null,"quantityLow":null,"unitCode":null,"unitText":null,"unitId":null,
            "ingredientId":"00000000-0000-0000-0000-000000000001","confidence":1,"note":null,"unparsed":false,"suggestions":[]},
            {"original":"2 dl melk","ingredientText":"melk","quantity":2,"quantityLow":null,"unitCode":"dl","unitText":null,"unitId":"00000000-0000-0000-0000-000000000002",
            "ingredientId":"00000000-0000-0000-0000-000000000001","confidence":1,"note":null,"unparsed":false,"suggestions":[]}],"warnings":[]}
            """;

        var draft = ImportDraftJson.Deserialize(legacy);

        Assert.Equal([AmountKind.ToTaste, AmountKind.Measured], draft.Lines.Select(l => l.AmountKind));
        Assert.All(draft.Lines, l => Assert.Equal(ImportLineStatus.Matched, l.Status));
    }

    [Fact]
    public void The_new_fields_survive_the_draft_json()
    {
        var draft = ImportDraftBuilder.Build(
            new ImportedRecipe("T", null, 4, null, null, null, ["2 dl melk eller fløte (kan sløyfes)", "smør til steking"], [], []), Matcher, UnitId);

        var back = ImportDraftJson.Deserialize(ImportDraftJson.Serialize(draft));

        Assert.Equal(draft.Lines.Select(l => (l.AmountKind, l.Purpose, l.IsOptional)), back.Lines.Select(l => (l.AmountKind, l.Purpose, l.IsOptional)));
        Assert.Equal(draft.Lines[0].Alternatives, back.Lines[0].Alternatives);
        Assert.Contains("\"amountKind\":\"Measured\"", ImportDraftJson.Serialize(draft), StringComparison.Ordinal);
    }
}
