using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Imports;

public class AiSuggestionMergerTests
{
    private static readonly IngredientMatcher Matcher = new(IngredientSeed.All.Select(r => new IngredientCandidate(r.Id, r.Nb, [r.Nb, r.Name], false)));
    private static readonly Guid Milk = IngredientSeed.All.Single(r => r.Key == "melk").Id;
    private static readonly Guid Butter = IngredientSeed.All.Single(r => r.Key == "smor").Id;
    private static readonly Guid Oil = IngredientSeed.All.Single(r => r.Key == "rapsolje").Id;

    private static Guid? UnitId(string code) => UnitSeed.All.SingleOrDefault(u => u.Code == code)?.Id;

    private static ImportDraftLine Unreadable(string original) =>
        new(original, null, null, null, null, null, null, null, 0, null, true, []);

    private static ImportDraftLine Matched(string original) =>
        new(original, "melk", 2, null, "dl", null, UnitId("dl"), Milk, 1, null, false, []);

    private static ImportDraft Draft(params ImportDraftLine[] lines) =>
        new("T", null, 4, null, null, null, [], lines, []);

    private static AiLineSuggestion Suggestion(
        string? ingredient = "melk", decimal? quantity = 2, string? unit = "dl", string? kind = "Measured", int index = 0,
        decimal? low = null, string? purpose = null, bool optional = false, string[]? alternatives = null, string? note = null) =>
        new(index, ingredient, quantity, low, unit, kind, purpose, optional, alternatives, note);

    private static AiMergeResult Merge(ImportDraft draft, params AiLineSuggestion?[] suggestions) =>
        AiSuggestionMerger.Merge(draft, AiSuggestionMerger.Unresolved(draft), suggestions, Matcher, UnitId);

    [Fact]
    public void Only_lines_that_are_not_matched_are_unresolved()
    {
        var draft = Draft(Matched("2 dl melk"), Unreadable("noe rart"), Matched("1 dl melk") with { UnitId = null, UnitText = "boks" });

        Assert.Equal([1, 2], AiSuggestionMerger.Unresolved(draft));
    }

    [Fact]
    public void Unresolved_is_capped_and_skips_blank_originals()
    {
        var draft = Draft([.. Enumerable.Range(0, 40).Select(i => Unreadable("linje " + i)), Unreadable("  ")]);

        Assert.Equal(AiSuggestionMerger.MaxLinesPerCall, AiSuggestionMerger.Unresolved(draft).Count);
    }

    [Fact]
    public void The_request_text_is_the_original_trimmed_without_control_characters_and_cut()
    {
        Assert.Equal("2 dl melk", AiSuggestionMerger.RequestText(Unreadable("  2 dl\u0000 melk\n")));
        Assert.Equal(AiSuggestionMerger.MaxLineTextLength, AiSuggestionMerger.RequestText(Unreadable(new string('x', 1000))).Length);
    }

    [Fact]
    public void A_valid_suggestion_fills_the_line_and_marks_it_ai_suggested()
    {
        var result = Merge(Draft(Unreadable("en kopp melkevann")), Suggestion());

        var line = result.Draft.Lines.Single();
        Assert.Equal(1, result.Applied);
        Assert.True(line.AiSuggested);
        Assert.Equal(ImportLineStatus.Matched, line.Status);
        Assert.Equal(Milk, line.IngredientId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(UnitId("dl"), line.UnitId);
        Assert.Equal("en kopp melkevann", line.Original);
        Assert.False(line.Unparsed);
    }

    [Fact]
    public void Lines_that_were_not_sent_and_the_rule_based_matches_are_untouched()
    {
        var kept = Matched("2 dl melk");
        var result = Merge(Draft(kept, Unreadable("noe")), Suggestion(ingredient: "smør", quantity: 1, unit: "ss"));

        Assert.Equal(kept, result.Draft.Lines[0]);
        Assert.False(result.Draft.Lines[0].AiSuggested);
        Assert.Equal(Butter, result.Draft.Lines[1].IngredientId);
    }

    [Fact]
    public void An_unknown_ingredient_is_dropped_and_the_rule_based_line_stays()
    {
        var original = Unreadable("noe rart");
        var result = Merge(Draft(original), Suggestion(ingredient: "dragefrukt-nektar"));

        Assert.Equal(original, result.Draft.Lines.Single());
        Assert.Equal((0, 1), (result.Applied, result.Dropped));
    }

    [Theory]
    [InlineData("kvarts")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_unit_is_dropped(string? unit)
    {
        var original = Unreadable("noe");
        var result = Merge(Draft(original), Suggestion(unit: unit));

        Assert.Equal(original, result.Draft.Lines.Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public void A_quantity_out_of_range_is_dropped(int quantity)
    {
        var original = Unreadable("noe");

        Assert.Equal(original, Merge(Draft(original), Suggestion(quantity: quantity)).Draft.Lines.Single());
    }

    [Fact]
    public void Measured_without_a_quantity_is_dropped_and_a_quantity_on_to_taste_is_dropped()
    {
        var original = Unreadable("noe");

        Assert.Equal(original, Merge(Draft(original), Suggestion(quantity: null)).Draft.Lines.Single());
        Assert.Equal(original, Merge(Draft(original), Suggestion(kind: "ToTaste")).Draft.Lines.Single());
        Assert.Equal(original, Merge(Draft(original), Suggestion(quantity: null, kind: "ToTaste")).Draft.Lines.Single());
    }

    [Fact]
    public void To_taste_and_as_needed_lines_have_no_amount()
    {
        var result = Merge(
            Draft(Unreadable("smør til steking"), Unreadable("salt")),
            Suggestion(ingredient: "smør", quantity: null, unit: null, kind: "AsNeeded", purpose: "Frying"),
            Suggestion(index: 1, ingredient: "salt", quantity: null, unit: null, kind: "ToTaste", purpose: "Seasoning"));

        Assert.Equal(2, result.Applied);
        Assert.Equal((AmountKind.AsNeeded, IngredientPurpose.Frying), (result.Draft.Lines[0].AmountKind, result.Draft.Lines[0].Purpose));
        Assert.All(result.Draft.Lines, l => Assert.Equal((null, null), (l.Quantity, l.UnitId)));
        Assert.All(result.Draft.Lines, l => Assert.Equal(ImportLineStatus.Matched, l.Status));
    }

    [Theory]
    [InlineData("Dessert", null)]
    [InlineData(null, "Sometimes")]
    public void An_unknown_purpose_or_kind_is_dropped(string? purpose, string? kind)
    {
        var original = Unreadable("noe");

        Assert.Equal(original, Merge(Draft(original), Suggestion(purpose: purpose, kind: kind ?? "Measured")).Draft.Lines.Single());
    }

    [Fact]
    public void A_range_needs_a_low_value_below_the_quantity()
    {
        var original = Unreadable("2-3 dl melk");

        var ok = Merge(Draft(original), Suggestion(quantity: 3, low: 2)).Draft.Lines.Single();
        Assert.Equal((2m, 3m), (ok.QuantityLow, ok.Quantity));
        Assert.Equal(original, Merge(Draft(original), Suggestion(quantity: 3, low: 4)).Draft.Lines.Single());
    }

    [Fact]
    public void Alternatives_become_ids_when_they_match_and_note_text_when_they_do_not()
    {
        var result = Merge(
            Draft(Unreadable("2 ss smør eller olje")),
            Suggestion(ingredient: "smør", quantity: 2, unit: "ss", alternatives: ["rapsolje", "kokosfett fra månen", "smør"], note: "myknet", optional: true));

        var line = result.Draft.Lines.Single();
        Assert.Equal([Oil], line.Alternatives);
        Assert.Equal("myknet; kokosfett fra månen", line.Note);
        Assert.True(line.IsOptional);
    }

    [Fact]
    public void Indexes_outside_the_request_duplicates_and_nulls_are_dropped()
    {
        var result = Merge(
            Draft(Unreadable("a")),
            Suggestion(index: 5),
            Suggestion(index: -1),
            null,
            Suggestion(),
            Suggestion(ingredient: "smør"));

        Assert.Equal(1, result.Applied);
        Assert.Equal(4, result.Dropped);
        Assert.Equal(Milk, result.Draft.Lines.Single().IngredientId);
    }

    [Fact]
    public void Names_with_control_characters_or_absurd_length_are_dropped()
    {
        var original = Unreadable("noe");

        Assert.Equal(original, Merge(Draft(original), Suggestion(ingredient: "mel\u0000k")).Draft.Lines.Single());
        Assert.Equal(original, Merge(Draft(original), Suggestion(ingredient: new string('m', 500))).Draft.Lines.Single());
    }

    [Fact]
    public void The_merge_does_not_set_the_assist_status_and_the_flag_survives_the_json_round_trip()
    {
        var merged = Merge(Draft(Unreadable("noe")), Suggestion()).Draft with { AiAssist = AiAssistStatus.Applied };

        var back = ImportDraftJson.Deserialize(ImportDraftJson.Serialize(merged));

        Assert.True(back.Lines.Single().AiSuggested);
        Assert.Equal(AiAssistStatus.Applied, back.AiAssist);
        Assert.Equal(AiAssistStatus.NotUsed, ImportDraftJson.Deserialize("{\"title\":\"x\",\"steps\":[],\"lines\":[],\"warnings\":[]}").AiAssist);
    }

    [Fact]
    public void Usage_periods_are_utc_day_and_first_of_month()
    {
        var now = new DateTimeOffset(2026, 11, 30, 23, 30, 0, TimeSpan.FromHours(-2)); // 01:30 UTC on 1 December

        Assert.Equal(new DateOnly(2026, 12, 1), AiUsagePeriod.Day(now));
        Assert.Equal(new DateOnly(2026, 12, 1), AiUsagePeriod.Month(now));
    }
}
