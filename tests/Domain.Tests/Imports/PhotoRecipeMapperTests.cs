using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Imports;

public class PhotoRecipeMapperTests
{
    private static readonly IngredientMatcher Matcher = new(IngredientSeed.All.Select(r => new IngredientCandidate(r.Id, r.Nb, [r.Nb, r.Name], false)));
    private static readonly Guid Salt = IngredientSeed.All.Single(r => r.Key == "salt").Id;
    private static readonly Guid Potato = IngredientSeed.All.Single(r => r.Key == "potet").Id;

    private static Guid? UnitId(string code) => UnitSeed.All.SingleOrDefault(u => u.Code == code)?.Id;

    private static PhotoIngredient Line(
        string ingredient = "Potet", string kind = "Measured", decimal? quantity = 4, decimal? low = null, string? unit = "stk",
        string purpose = "Main", bool optional = false, string[]? alternatives = null, string? note = null, string? original = null) =>
        new(original ?? $"{quantity} {unit} {ingredient}", ingredient, kind, quantity, low, unit, purpose, optional, alternatives, note);

    private static PhotoRecipe Page(
        PhotoIngredient[]? lines = null, string? title = "Bomba torsk", int? servings = 2, int? prep = null, int? cook = 20, string? description = null,
        string? book = null, string[]? steps = null, PhotoPicture? picture = null, string[]? warnings = null) =>
        new(true, true, title, servings, prep, cook, description, book, "raw page text", steps ?? ["Stek."], lines ?? [Line()], picture, warnings ?? []);

    private static PhotoMapResult Map(PhotoRecipe recipe, string? typed = null, int photos = 2) =>
        PhotoRecipeMapper.Map(recipe, typed, photos, Matcher, UnitId);

    private static ImportDraftLine One(PhotoIngredient line) => Assert.Single(Map(Page([line])).Draft.Lines);

    [Fact]
    public void A_valid_line_is_matched_structured_and_marked_ai()
    {
        var line = One(Line("Potet", quantity: 4, unit: "stk", note: "småpoteter"));

        Assert.Equal(ImportLineStatus.Matched, line.Status);
        Assert.Equal(Potato, line.IngredientId);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(UnitId("stk"), line.UnitId);
        Assert.Equal("småpoteter", line.Note); // the variant lands in the note
        Assert.True(line.AiSuggested);
        Assert.Equal("4 stk Potet", line.Original);
    }

    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(1.5, "1.5")]
    public void Fractions_are_numbers(double value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), One(Line(quantity: (decimal)value, unit: "dl")).Quantity);

    [Fact]
    public void A_range_has_the_lower_bound_in_low_and_the_upper_in_quantity()
    {
        var line = One(Line(quantity: 12, low: 8, unit: "stk", note: "8-12"));

        Assert.Equal(ImportLineStatus.Matched, line.Status);
        Assert.Equal(8m, line.QuantityLow);
        Assert.Equal(12m, line.Quantity);
    }

    [Fact]
    public void A_low_that_is_not_below_the_quantity_is_not_matched()
    {
        var line = One(Line(quantity: 8, low: 12, unit: "stk"));

        Assert.NotEqual(ImportLineStatus.Matched, line.Status);
        Assert.True(line.AiSuggested);
    }

    [Fact]
    public void A_can_is_one_stk_with_the_word_in_the_note()
    {
        var line = One(Line("Tomat", quantity: 1, unit: "stk", note: "boks"));

        Assert.Equal(ImportLineStatus.Matched, line.Status);
        Assert.Equal("boks", line.Note);
        Assert.Equal(UnitId("stk"), line.UnitId);
    }

    [Fact]
    public void Salt_and_pepper_are_two_to_taste_seasoning_lines_and_til_steking_is_as_needed_frying()
    {
        var draft = Map(Page([
            Line("Salt", "ToTaste", null, null, null, "Seasoning"),
            Line("Pepper", "ToTaste", null, null, null, "Seasoning"),
            Line("Smør", "AsNeeded", null, null, null, "Frying", note: "til steking"),
        ])).Draft;

        Assert.Equal(3, draft.Lines.Count);
        Assert.All(draft.Lines.Take(2), l => Assert.Equal((AmountKind.ToTaste, IngredientPurpose.Seasoning), (l.AmountKind, l.Purpose)));
        Assert.Equal(Salt, draft.Lines[0].IngredientId);
        Assert.Equal((AmountKind.AsNeeded, IngredientPurpose.Frying), (draft.Lines[2].AmountKind, draft.Lines[2].Purpose));
        Assert.All(draft.Lines, l => Assert.Null(l.Quantity));
    }

    [Fact]
    public void The_group_stays_in_the_note_and_optional_is_kept()
    {
        var line = One(Line("Salt", "ToTaste", null, null, null, "Serving", optional: true, note: "til sausen"));

        Assert.Equal("til sausen", line.Note);
        Assert.True(line.IsOptional);
        Assert.Equal(IngredientPurpose.Serving, line.Purpose);
    }

    [Fact]
    public void A_unit_outside_the_catalog_is_kept_as_an_unmatched_unit()
    {
        var line = One(Line("Potet", quantity: 2, unit: "boks"));

        Assert.Equal(ImportLineStatus.UnmatchedUnit, line.Status);
        Assert.Equal(Potato, line.IngredientId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("boks", line.UnitText);
        Assert.Null(line.UnitId);
        Assert.True(line.AiSuggested);
    }

    [Fact]
    public void An_ingredient_that_does_not_match_is_kept_with_its_page_text_and_suggestions()
    {
        var line = One(Line("Kryptonittsaus", quantity: 1, unit: "dl", original: "1 dl kryptonittsaus"));

        Assert.Equal(ImportLineStatus.UnmatchedIngredient, line.Status);
        Assert.Equal("Kryptonittsaus", line.IngredientText);
        Assert.Equal("1 dl kryptonittsaus", line.Original);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(UnitId("dl"), line.UnitId);
        Assert.True(line.AiSuggested);
    }

    [Theory]
    [InlineData("Weird", "Main", 4, "stk")] // kind not in the enum
    [InlineData("Measured", "Whatever", 4, "stk")] // purpose not in the enum
    [InlineData("Measured", "Main", -3, "stk")] // negative
    [InlineData("Measured", "Main", 1000000, "stk")] // huge
    [InlineData("Measured", "Main", 0, "stk")] // zero
    public void Invalid_values_are_never_matched_but_the_line_is_kept(string kind, string purpose, int quantity, string unit)
    {
        var draft = Map(Page([Line("Potet", kind, quantity, null, unit, purpose)])).Draft;

        var line = Assert.Single(draft.Lines);
        Assert.NotEqual(ImportLineStatus.Matched, line.Status);
        Assert.True(line.AiSuggested);
        Assert.Equal("Potet", line.IngredientText);
    }

    [Fact]
    public void To_taste_with_an_amount_is_not_matched()
    {
        Assert.NotEqual(ImportLineStatus.Matched, One(Line("Salt", "ToTaste", 1, null, "ts")).Status);
    }

    [Fact]
    public void An_empty_or_control_character_name_is_kept_as_unparsed_text()
    {
        var line = One(Line("   ", original: "illegible scribble"));

        Assert.Equal(ImportLineStatus.UnmatchedIngredient, line.Status);
        Assert.Equal("illegible scribble", line.Original);
    }

    [Fact]
    public void Alternatives_become_ids_or_note_text_and_the_note_is_cut_to_200()
    {
        var line = One(Line("Smør", quantity: 2, unit: "ss", alternatives: ["Salt", "Zorglub"], note: new string('x', 300)));

        Assert.Equal(ImportLineStatus.Matched, line.Status);
        Assert.Contains(Salt, line.Alternatives);
        Assert.True(line.Note!.Length <= RecipeIngredient.MaxNoteLength);
    }

    [Fact]
    public void Lines_above_the_maximum_are_clipped_with_a_warning()
    {
        var lines = Enumerable.Range(0, Recipe.MaxIngredients + 5).Select(_ => Line()).ToArray();
        var steps = Enumerable.Range(0, Recipe.MaxSteps + 3).Select(i => $"Steg {i}").ToArray();

        var draft = Map(Page(lines, steps: steps)).Draft;

        Assert.Equal(Recipe.MaxIngredients, draft.Lines.Count);
        Assert.Equal(Recipe.MaxSteps, draft.Steps.Count);
        Assert.Contains(draft.Warnings, w => w.Contains("ingredient", StringComparison.Ordinal));
        Assert.Contains(draft.Warnings, w => w.Contains("steps", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_servings_gives_four_with_a_warning_and_a_missing_title_a_warning()
    {
        var draft = Map(Page(servings: null, title: "  ")).Draft;

        Assert.Equal(ImportDraftBuilder.DefaultServings, draft.Servings);
        Assert.Contains(draft.Warnings, w => w.Contains("servings", StringComparison.Ordinal));
        Assert.Contains(draft.Warnings, w => w.Contains("title", StringComparison.Ordinal));
    }

    [Fact]
    public void Minutes_above_the_maximum_are_left_out_with_a_warning()
    {
        var draft = Map(Page(cook: Recipe.MaxMinutes + 1, prep: 15)).Draft;

        Assert.Null(draft.CookMinutes);
        Assert.Equal(15, draft.PrepMinutes);
        Assert.Contains(draft.Warnings, w => w.Contains("cooking time", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tip_is_the_description_cut_to_the_maximum_and_the_draft_is_an_applied_ai_draft()
    {
        var draft = Map(Page(description: new string('t', Recipe.MaxDescriptionLength + 50))).Draft;

        Assert.Equal(Recipe.MaxDescriptionLength, draft.Description!.Length);
        Assert.Equal(AiAssistStatus.Applied, draft.AiAssist);
        Assert.Null(draft.ImageUrl);
    }

    [Fact]
    public void The_book_typed_by_the_person_wins_and_is_cut_to_100()
    {
        Assert.Equal("Typed", Map(Page(book: "On the page"), "Typed").Draft.SourceName);
        Assert.Equal("On the page", Map(Page(book: "On the page")).Draft.SourceName);
        Assert.Null(Map(Page(book: null)).Draft.SourceName);

        var cut = Map(Page(), new string('b', 140));
        Assert.Equal(Recipe.MaxSourceNameLength, cut.Draft.SourceName!.Length);
        Assert.Contains(cut.Draft.Warnings, w => w.Contains("book name", StringComparison.Ordinal));
    }

    [Fact]
    public void The_page_text_is_the_raw_content_and_warnings_from_the_reader_are_kept_cut_and_limited()
    {
        var recipe = Page(warnings: [.. Enumerable.Range(0, 20).Select(i => i == 0 ? "multiple-recipes" : new string('w', 400))]);
        var mapped = Map(recipe);

        Assert.Equal("raw page text", mapped.RawText);
        Assert.Equal("multiple-recipes", mapped.Draft.Warnings[0]);
        Assert.True(mapped.Draft.Warnings.Count <= PhotoRecipeMapper.MaxWarnings);
        Assert.All(mapped.Draft.Warnings, w => Assert.True(w.Length <= PhotoRecipeMapper.MaxWarningLength));
    }

    [Fact]
    public void A_valid_picture_box_is_returned_as_the_crop()
    {
        var mapped = Map(Page(picture: new PhotoPicture(1, 0.1, 0.2, 0.5, 0.4, 90)));

        Assert.Equal(new PictureCrop(1, 0.1, 0.2, 0.5, 0.4, 90), mapped.Crop);
    }

    // ---- PictureCrop -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-0.1, 0.1, 0.5, 0.5, 0, 0)] // outside 0..1
    [InlineData(0.1, 0.1, 1.5, 0.5, 0, 0)]
    [InlineData(double.NaN, 0.1, 0.5, 0.5, 0, 0)]
    [InlineData(0.1, double.PositiveInfinity, 0.5, 0.5, 0, 0)]
    [InlineData(0.1, 0.1, 0.09, 0.5, 0, 0)] // w under 0.10
    [InlineData(0.1, 0.1, 0.5, 0.05, 0, 0)] // h under 0.10
    [InlineData(0.6, 0.1, 0.5, 0.5, 0, 0)] // x + w over 1.001
    [InlineData(0.1, 0.6, 0.5, 0.5, 0, 0)] // y + h over 1.001
    [InlineData(0.1, 0.1, 0.5, 0.5, 45, 0)] // rotation
    [InlineData(0.1, 0.1, 0.5, 0.5, 0, 2)] // photo index not sent (2 photos: 0 and 1)
    [InlineData(0.1, 0.1, 0.5, 0.5, 0, -1)]
    public void An_invalid_box_gives_no_picture(double x, double y, double w, double h, int rotation, int photoIndex) =>
        Assert.Null(PictureCrop.From(new PhotoPicture(photoIndex, x, y, w, h, rotation), 2));

    [Fact]
    public void A_box_a_hair_over_the_edge_is_clamped_and_none_is_none()
    {
        var crop = PictureCrop.From(new PhotoPicture(0, 0.5, 0.0, 0.5005, 1.0, 270), 1);

        Assert.NotNull(crop);
        Assert.Equal(0.5, crop.W, 6);
        Assert.Equal(270, crop.RotationDegrees);
        Assert.Null(PictureCrop.From(null, 1));
    }
}
