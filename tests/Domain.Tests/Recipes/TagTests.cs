using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Recipes;

public class TagTests
{
    [Fact]
    public void Seed_has_unique_valid_categories_and_tags()
    {
        var all = TagSeed.All;

        Assert.Equal(all.Count, all.Select(r => r.Key).Distinct().Count());
        Assert.Equal(all.Count, all.Select(r => r.Id).Distinct().Count());
        Assert.Equal(all.Count, all.Select(r => r.Name.ToUpperInvariant()).Distinct().Count()); // the database compares case-insensitively
        Assert.All(all, r =>
        {
            Assert.Equal(r.Name, Tag.NormalizeName(r.Name));
            Assert.Matches("^[a-z0-9_]+$", r.Key);
            Assert.True(Enum.IsDefined(r.Kind));
        });
        Assert.Contains(all, r => r.Kind == TagKind.Category && r.Name == "Middag");
        Assert.Contains(all, r => r.Kind == TagKind.Tag && r.Name == "Barnevennlig");
    }

    [Theory]
    [InlineData("  Rask   middag ", "Rask middag")]
    [InlineData("Vegetar", "Vegetar")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("a\tb\u0001", null)]
    public void NormalizeName_trims_collapses_and_rejects(string input, string? expected) =>
        Assert.Equal(expected, Tag.NormalizeName(input));

    [Fact]
    public void NormalizeName_rejects_too_long_and_null()
    {
        Assert.Null(Tag.NormalizeName(new string('x', Tag.MaxNameLength + 1)));
        Assert.NotNull(Tag.NormalizeName(new string('x', Tag.MaxNameLength)));
        Assert.Null(Tag.NormalizeName(null));
    }

    [Fact]
    public void A_household_tag_starts_with_its_household_and_validates_its_input()
    {
        var household = Guid.NewGuid();

        var tag = new Tag(household, " Grill ", TagKind.Category);

        Assert.Equal(household, tag.HouseholdId);
        Assert.Equal("Grill", tag.Name);
        Assert.Throws<ArgumentException>(() => new Tag(household, " ", TagKind.Tag));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tag(household, "X", (TagKind)99));
    }

    [Theory]
    [InlineData("Category", true, TagKind.Category)]
    [InlineData("tag", true, TagKind.Tag)]
    [InlineData("1", false, default(TagKind))]
    [InlineData(" Tag", false, default(TagKind))]
    [InlineData(null, false, default(TagKind))]
    public void Kind_parsing_is_strict(string? input, bool ok, TagKind expected)
    {
        Assert.Equal(ok, TagKinds.TryParse(input, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void A_recipe_tag_needs_both_ids()
    {
        var recipe = Guid.NewGuid();
        var tag = Guid.NewGuid();

        var link = new RecipeTag(recipe, tag);

        Assert.Equal(recipe, link.RecipeId);
        Assert.Equal(tag, link.TagId);
        Assert.Throws<ArgumentException>(() => new RecipeTag(Guid.Empty, tag));
        Assert.Throws<ArgumentException>(() => new RecipeTag(recipe, Guid.Empty));
    }
}
