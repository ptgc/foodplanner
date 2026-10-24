using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Tests.Catalog;

public class IngredientTests
{
    [Fact]
    public void Seed_has_about_a_hundred_unique_ingredients()
    {
        var all = IngredientSeed.All;

        Assert.InRange(all.Count, 90, 130);
        Assert.Equal(all.Count, all.Select(r => r.Key).Distinct().Count());
        Assert.Equal(all.Count, all.Select(r => r.Id).Distinct().Count());
        Assert.Equal(all.Count, all.Select(r => r.Name.ToUpperInvariant()).Distinct().Count()); // the database compares case-insensitively
        Assert.All(all, r =>
        {
            Assert.Equal(r.Name, Ingredient.NormalizeName(r.Name)); // every seeded name is valid and normalized
            Assert.Matches("^[a-z0-9_]+$", r.Key);
            Assert.True(Enum.IsDefined(r.Dimension));
        });
    }

    [Theory]
    [InlineData("salt", true)]
    [InlineData("sort_pepper", true)]
    [InlineData("sukker", true)]
    [InlineData("hvetemel", true)]
    [InlineData("rapsolje", true)]
    [InlineData("olivenolje", true)]
    [InlineData("smor", true)]
    [InlineData("kyllingfilet", false)]
    [InlineData("melk", false)]
    [InlineData("egg", false)]
    public void Staple_flags(string key, bool staple) =>
        Assert.Equal(staple, IngredientSeed.All.Single(r => r.Key == key).IsStaple);

    [Fact]
    public void Staples_are_a_small_minority()
    {
        var staples = IngredientSeed.All.Count(r => r.IsStaple);

        Assert.InRange(staples, 8, 20);
    }

    [Fact]
    public void Seed_ids_are_stable_and_follow_RFC_4122_v5()
    {
        // The RFC 4122 / Python uuid5 vector: uuid5(NAMESPACE_DNS, "python.org").
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), SeedIds.NameBased(dns, "python.org"));

        Assert.Equal(SeedIds.For("ingredient", "salt"), IngredientSeed.All.Single(r => r.Key == "salt").Id);
        Assert.NotEqual(SeedIds.For("ingredient", "g"), SeedIds.For("unit", "g")); // kinds do not collide
        Assert.Equal(new Guid("e0ba0668-a3ec-50ba-9063-115da67c2e6a"), IngredientSeed.All.Single(r => r.Key == "salt").Id);
        Assert.Equal(new Guid("21341ca2-75fd-5c45-864a-084b06676bc3"), UnitSeed.All.Single(r => r.Code == "g").Id);
    }

    [Theory]
    [InlineData("  Melk  ", "Melk")]
    [InlineData("Hermetiske   tomater", "Hermetiske tomater")]
    [InlineData("Fløte", "Fløte")]
    public void Names_are_trimmed_and_whitespace_collapsed(string input, string expected) =>
        Assert.Equal(expected, Ingredient.NormalizeName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007name")]
    public void Invalid_names_are_rejected(string? input) => Assert.Null(Ingredient.NormalizeName(input));

    [Fact]
    public void Name_length_boundary()
    {
        Assert.NotNull(Ingredient.NormalizeName(new string('x', Ingredient.MaxNameLength)));
        Assert.Null(Ingredient.NormalizeName(new string('x', Ingredient.MaxNameLength + 1)));
    }

    [Fact]
    public void A_household_ingredient_carries_its_household()
    {
        var household = Guid.NewGuid();

        var ingredient = new Ingredient(household, " Surdeig ", false, UnitDimension.Mass);

        Assert.Equal(household, ingredient.HouseholdId);
        Assert.Equal("Surdeig", ingredient.Name);
        Assert.Throws<ArgumentException>(() => new Ingredient(household, " ", false, UnitDimension.Mass));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ingredient(household, "x", false, (UnitDimension)0));
    }
}
