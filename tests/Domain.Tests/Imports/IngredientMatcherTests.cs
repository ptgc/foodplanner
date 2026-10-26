using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;

namespace Foodplanner.Domain.Tests.Imports;

public class IngredientMatcherTests
{
    private static Guid Id(string key) => IngredientSeed.All.Single(r => r.Key == key).Id;

    /// <summary>The seeded catalog as a household sees it: every row with its nb and en name.</summary>
    private static List<IngredientCandidate> Seed() =>
        [.. IngredientSeed.All.Select(r => new IngredientCandidate(r.Id, r.Nb, [r.Nb, r.Name], IsOwn: false))];

    private static IngredientMatcher Matcher(params IngredientCandidate[] extra) => new([.. Seed(), .. extra]);

    [Theory]
    [InlineData("melk", "melk")]
    [InlineData("Melk", "melk")]
    [InlineData("  MELK. ", "melk")]
    [InlineData("hvetemel", "hvetemel")]
    [InlineData("Smør", "smor")]
    public void Exact_norwegian_name_matches_with_full_confidence(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.Equal(1.0, match.Confidence);
    }

    [Theory]
    [InlineData("Milk", "melk")]
    [InlineData("butter", "smor")]
    [InlineData("wheat flour", "hvetemel")]
    [InlineData("Olive oil", "olivenolje")]
    [InlineData("black pepper", "sort_pepper")]
    [InlineData("garlic", "hvitloek")]
    public void Exact_english_name_matches_with_full_confidence(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.Equal(1.0, match.Confidence);
    }

    [Theory]
    [InlineData("tomater", "tomat")]
    [InlineData("poteter", "potet")]
    [InlineData("gulrøtter", "gulrot")]
    [InlineData("eggs", "egg")]
    [InlineData("onions", "loek")]
    [InlineData("løker", "loek")]
    [InlineData("kidneybønne", "kidneybonner")]
    public void Plurals_and_spelling_variants_match_after_stemming(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.InRange(match.Confidence, 0.9, 0.99);
    }

    [Fact]
    public void Words_written_apart_match_a_compound_name()
    {
        var match = Matcher().Match("rød løk");

        Assert.Equal(Id("rodloek"), match.IngredientId);
        Assert.Equal(0.93, match.Confidence);
    }

    [Theory]
    [InlineData("hakkede tomater", "tomat")]
    [InlineData("fersk persille", "persille")]
    [InlineData("finhakket hvitløk", "hvitloek")]
    [InlineData("hermetiske hakkede tomater", "hermetiske_tomater")]
    [InlineData("freshly chopped parsley", "persille")]
    [InlineData("revet ost", "ost")]
    public void Descriptors_are_ignored(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.InRange(match.Confidence, Math.Min(IngredientMatcher.Threshold, 0.9), 1.0);
    }

    [Theory]
    [InlineData("rød paprika", "paprika")]
    [InlineData("yoghurt", "yoghurt")]
    [InlineData("naturell yoghurt", "yoghurt")]
    public void Words_found_in_a_longer_name_or_text_match_at_the_threshold(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.True(match.Confidence >= IngredientMatcher.Threshold);
    }

    [Theory]
    [InlineData("spagetti", "spaghetti")]
    [InlineData("brokkolli", "brokkoli")]
    [InlineData("mozarella", "mozzarella")]
    [InlineData("parmasan", "parmesan")]
    public void A_close_misspelling_of_a_long_name_matches_with_lower_confidence(string text, string key)
    {
        var match = Matcher().Match(text);

        Assert.Equal(Id(key), match.IngredientId);
        Assert.InRange(match.Confidence, IngredientMatcher.Threshold, 0.94);
    }

    [Theory]
    [InlineData("dragefrukt")]
    [InlineData("ose")]
    [InlineData("xanthangummi")]
    [InlineData("kyllingbuljong")]
    [InlineData("mel")]
    public void Text_below_the_threshold_is_unmatched(string text)
    {
        var match = Matcher().Match(text);

        Assert.Null(match.IngredientId);
        Assert.Null(match.Name);
        Assert.Equal(0, match.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Empty_text_is_unmatched_without_suggestions(string? text)
    {
        var match = Matcher().Match(text);

        Assert.Null(match.IngredientId);
        Assert.Empty(match.Suggestions);
    }

    [Fact]
    public void Two_different_ingredients_with_the_same_loose_score_stay_unmatched_and_are_suggested()
    {
        var match = Matcher().Match("oil");

        Assert.Null(match.IngredientId);
        Assert.Contains(match.Suggestions, s => s.IngredientId == Id("olivenolje"));
        Assert.Contains(match.Suggestions, s => s.IngredientId == Id("rapsolje"));
    }

    [Fact]
    public void An_unmatched_text_still_offers_suggestions()
    {
        var match = Matcher().Match("pepper");

        Assert.Null(match.IngredientId);
        Assert.Contains(match.Suggestions, s => s.IngredientId == Id("sort_pepper"));
        Assert.All(match.Suggestions, s => Assert.InRange(s.Confidence, 0.5, 0.99));
        Assert.True(match.Suggestions.Count <= 3);
    }

    [Fact]
    public void A_household_ingredient_is_a_candidate()
    {
        var own = new IngredientCandidate(Guid.NewGuid(), "Mormors kjøttkaker", ["Mormors kjøttkaker"], IsOwn: true);

        var match = Matcher(own).Match("mormors kjøttkaker");

        Assert.Equal(own.Id, match.IngredientId);
        Assert.Equal("Mormors kjøttkaker", match.Name);
    }

    [Fact]
    public void At_an_equal_score_the_household_ingredient_wins_over_the_global_one()
    {
        var own = new IngredientCandidate(Guid.NewGuid(), "Egg", ["Egg"], IsOwn: true);

        var match = Matcher(own).Match("egg");

        Assert.Equal(own.Id, match.IngredientId);
    }

    [Fact]
    public void Only_the_candidates_it_was_given_can_be_matched()
    {
        // The caller passes the household's visible ingredients; another household's ingredient never reaches the matcher.
        var matcher = new IngredientMatcher([new IngredientCandidate(Id("melk"), "Melk", ["Melk"], IsOwn: false)]);

        Assert.Null(matcher.Match("Mormors kjøttkaker").IngredientId);
        Assert.Equal(Id("melk"), matcher.Match("melk").IngredientId);
    }

    [Fact]
    public void A_matched_result_lists_runner_ups_but_not_the_match_itself()
    {
        var match = Matcher().Match("tomater");

        Assert.DoesNotContain(match.Suggestions, s => s.IngredientId == match.IngredientId);
    }

    [Fact]
    public void Matching_is_deterministic_regardless_of_candidate_order()
    {
        var forward = new IngredientMatcher(Seed()).Match("oil");
        var backward = new IngredientMatcher(Enumerable.Reverse(Seed())).Match("oil");

        Assert.Equal(forward.Suggestions.Select(s => s.IngredientId), backward.Suggestions.Select(s => s.IngredientId));
    }

    [Fact]
    public void Every_seeded_name_in_both_languages_matches_itself_exactly()
    {
        var matcher = Matcher();
        foreach (var row in IngredientSeed.All)
        {
            foreach (var name in new[] { row.Nb, row.Name })
            {
                var match = matcher.Match(name);
                Assert.True(match.IngredientId is not null, $"'{name}' did not match");
                Assert.Equal(1.0, match.Confidence);
            }
        }
    }
}
