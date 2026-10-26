using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Imports;

public class ImportDraftTests
{
    private static readonly IngredientMatcher Matcher = new(IngredientSeed.All.Select(r => new IngredientCandidate(r.Id, r.Nb, [r.Nb, r.Name], false)));

    private static Guid? UnitId(string code) => UnitSeed.All.SingleOrDefault(u => u.Code == code)?.Id;

    private static ImportedRecipe Imported(
        IEnumerable<string>? lines = null, IEnumerable<string>? steps = null, int? servings = 4, int? prep = null, int? cook = null, string? title = "Pannekaker") =>
        new(title, null, servings, prep, cook, null, [.. lines ?? []], [.. steps ?? []], []);

    [Fact]
    public void Lines_are_parsed_matched_and_their_units_mapped()
    {
        var draft = ImportDraftBuilder.Build(Imported(["2 dl melk", "3 egg", "salt", "1 boks hakkede tomater", "2 dl dragefrukt", "???"]), Matcher, UnitId);

        Assert.Equal(
            [ImportLineStatus.Matched, ImportLineStatus.Matched, ImportLineStatus.Matched, ImportLineStatus.UnmatchedUnit, ImportLineStatus.UnmatchedIngredient, ImportLineStatus.Unparsed],
            draft.Lines.Select(l => l.Status));
        Assert.False(draft.AllLinesMatched);

        var milk = draft.Lines[0];
        Assert.Equal(IngredientSeed.All.Single(r => r.Key == "melk").Id, milk.IngredientId);
        Assert.Equal(2m, milk.Quantity);
        Assert.Equal(UnitSeed.All.Single(u => u.Code == "dl").Id, milk.UnitId);
        Assert.Equal("2 dl melk", milk.Original);

        var salt = draft.Lines[2];
        Assert.Null(salt.Quantity);
        Assert.Null(salt.UnitId); // to taste

        var tomatoes = draft.Lines[3];
        Assert.Equal("boks", tomatoes.UnitText);
        Assert.NotNull(tomatoes.IngredientId); // the ingredient matched, the unit did not

        var unreadable = draft.Lines[5];
        Assert.True(unreadable.Unparsed);
        Assert.Equal("???", unreadable.Original);
    }

    [Fact]
    public void A_unit_code_missing_from_the_catalog_is_an_unmatched_unit()
    {
        var draft = ImportDraftBuilder.Build(Imported(["2 dl melk"]), Matcher, _ => null);

        Assert.Equal(ImportLineStatus.UnmatchedUnit, draft.Lines.Single().Status);
        Assert.Equal("dl", draft.Lines.Single().UnitCode);
    }

    [Fact]
    public void Salt_og_pepper_gives_two_lines_and_the_ambiguous_one_needs_a_person()
    {
        var draft = ImportDraftBuilder.Build(Imported(["2 dl melk", "salt og pepper"], ["Bland"]), Matcher, UnitId);

        // "pepper" is ambiguous in the seed (Sort pepper, Bell pepper), so one line still needs a person.
        Assert.False(draft.AllLinesMatched);
        Assert.Equal(ImportLineStatus.UnmatchedIngredient, draft.Lines.Last().Status);
        Assert.Equal(3, draft.Lines.Count);
    }

    [Fact]
    public void Missing_servings_default_to_four_with_a_warning()
    {
        var draft = ImportDraftBuilder.Build(Imported(servings: null), Matcher, UnitId);

        Assert.Equal(ImportDraftBuilder.DefaultServings, draft.Servings);
        Assert.Contains(draft.Warnings, w => w.Contains("servings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_title_is_empty_with_a_warning()
    {
        var draft = ImportDraftBuilder.Build(Imported(title: "  "), Matcher, UnitId);

        Assert.Equal(string.Empty, draft.Title);
        Assert.Contains(draft.Warnings, w => w.Contains("title", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Times_longer_than_a_day_are_left_out_with_a_warning()
    {
        var draft = ImportDraftBuilder.Build(Imported(prep: Recipe.MaxMinutes + 1, cook: 30), Matcher, UnitId);

        Assert.Null(draft.PrepMinutes);
        Assert.Equal(30, draft.CookMinutes);
        Assert.Contains(draft.Warnings, w => w.Contains("prep time", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_many_lines_and_steps_are_cut_with_warnings()
    {
        var lines = Enumerable.Range(0, Recipe.MaxIngredients + 5).Select(i => $"{i + 1} dl melk");
        var steps = Enumerable.Range(0, Recipe.MaxSteps + 5).Select(i => $"Steg nummer {i}");

        var draft = ImportDraftBuilder.Build(Imported(lines, steps), Matcher, UnitId);

        Assert.Equal(Recipe.MaxIngredients, draft.Lines.Count);
        Assert.Equal(Recipe.MaxSteps, draft.Steps.Count);
        Assert.Equal(2, draft.Warnings.Count);
    }

    [Fact]
    public void Steps_are_cleaned_and_empty_ones_dropped()
    {
        var draft = ImportDraftBuilder.Build(Imported(steps: ["1. Bland <b>alt</b>&nbsp;godt", "   ", "Stek &amp; server"]), Matcher, UnitId);

        Assert.Equal(["Bland alt godt", "Stek & server"], draft.Steps);
    }

    [Fact]
    public void The_draft_survives_a_json_round_trip_with_its_status()
    {
        var draft = ImportDraftBuilder.Build(Imported(["2 dl melk", "1 boks tomater", "???"], ["Bland"], prep: 10, cook: 20), Matcher, UnitId);

        var back = ImportDraftJson.Deserialize(ImportDraftJson.Serialize(draft));

        Assert.Equal(draft.Title, back.Title);
        Assert.Equal(draft.Steps, back.Steps);
        Assert.Equal(draft.Lines.Select(l => l.Status), back.Lines.Select(l => l.Status));
        Assert.Equal(draft.Lines.Select(l => l.Original), back.Lines.Select(l => l.Original));
        Assert.Equal(draft.Lines[0].Quantity, back.Lines[0].Quantity);
        Assert.Equal(draft.Lines[0].UnitId, back.Lines[0].UnitId);
        Assert.Equal(10, back.PrepMinutes);
    }

    [Fact]
    public void Json_does_not_contain_the_computed_status()
    {
        var draft = ImportDraftBuilder.Build(Imported(["2 dl melk"]), Matcher, UnitId);

        var json = ImportDraftJson.Serialize(draft);

        Assert.DoesNotContain("\"status\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("allLinesMatched", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_status_follows_the_ids()
    {
        var line = new ImportDraftLine("x", "x", 1m, null, "dl", null, null, Guid.NewGuid(), 1, null, false, []);

        Assert.Equal(ImportLineStatus.UnmatchedUnit, line.Status);
        Assert.Equal(ImportLineStatus.Matched, (line with { UnitId = Guid.NewGuid() }).Status);
        Assert.Equal(ImportLineStatus.Matched, (line with { Quantity = null }).Status);
        Assert.Equal(ImportLineStatus.UnmatchedUnit, (line with { Quantity = null, UnitId = Guid.NewGuid() }).Status);
        Assert.Equal(ImportLineStatus.UnmatchedIngredient, (line with { IngredientId = null }).Status);
        Assert.Equal(ImportLineStatus.Unparsed, (line with { IngredientId = null, Unparsed = true }).Status);
    }
}

public class RecipeImportEntityTests
{
    private static RecipeImport NewImport() => new(ImportSource.Text, null, "raw", "{}", Guid.NewGuid());

    [Fact]
    public void A_new_import_is_a_draft_with_its_raw_text()
    {
        var import = NewImport();

        Assert.Equal(ImportStatus.Draft, import.Status);
        Assert.Equal("raw", import.RawContent);
        Assert.Null(import.SavedRecipeId);
    }

    [Fact]
    public void Raw_content_is_capped()
    {
        var import = new RecipeImport(ImportSource.Text, null, new string('x', RecipeImport.MaxRawLength + 100), "{}", Guid.NewGuid());

        Assert.Equal(RecipeImport.MaxRawLength, import.RawContent!.Length);
    }

    [Fact]
    public void A_url_import_needs_a_url_and_a_user()
    {
        Assert.Throws<ArgumentException>(() => new RecipeImport(ImportSource.Url, null, "raw", "{}", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new RecipeImport(ImportSource.Text, null, "raw", "{}", Guid.Empty));
        Assert.Throws<ArgumentException>(() => new RecipeImport(ImportSource.Text, null, "raw", " ", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new RecipeImport(ImportSource.Url, new string('a', RecipeImport.MaxUrlLength + 1), "raw", "{}", Guid.NewGuid()));
    }

    [Fact]
    public void Saving_records_the_recipe_and_ends_editing()
    {
        var import = NewImport();
        var recipe = Guid.NewGuid();

        import.MarkSaved(recipe);

        Assert.Equal(ImportStatus.Saved, import.Status);
        Assert.Equal(recipe, import.SavedRecipeId);
        Assert.Equal("raw", import.RawContent); // the artifact is kept
        Assert.Throws<InvalidOperationException>(() => import.UpdateDraft("{}"));
        Assert.Throws<InvalidOperationException>(() => import.MarkSaved(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(import.Discard);
    }

    [Fact]
    public void Discarding_erases_the_original_text_and_the_draft()
    {
        var import = NewImport();

        import.Discard();

        Assert.Equal(ImportStatus.Discarded, import.Status);
        Assert.Null(import.RawContent);
        Assert.Null(import.DraftJson);
        Assert.Throws<InvalidOperationException>(() => import.MarkSaved(Guid.NewGuid()));
    }

    [Fact]
    public void A_draft_can_be_updated_while_it_is_a_draft()
    {
        var import = NewImport();

        import.UpdateDraft("{\"a\":1}");

        Assert.Equal("{\"a\":1}", import.DraftJson);
        Assert.Throws<ArgumentException>(() => import.UpdateDraft(" "));
    }
}
