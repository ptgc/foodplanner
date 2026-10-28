using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class StoreSectionOrderTests
{
    private sealed record Row(string Name, StoreSection Section, Guid Id);

    private static Row R(string name, StoreSection section) => new(name, section, Guid.NewGuid());

    [Fact]
    public void The_default_order_is_produce_counter_frozen_dairy_dry_goods_other()
    {
        Assert.Equal(
            [
                StoreSection.FruitAndVegetables,
                StoreSection.MeatFishAndCheese,
                StoreSection.Frozen,
                StoreSection.Dairy,
                StoreSection.DryGoods,
                StoreSection.Other,
            ],
            StoreSectionOrder.Default);
    }

    [Fact]
    public void The_default_order_names_every_section_exactly_once()
    {
        Assert.Equal(Enum.GetValues<StoreSection>().Order(), StoreSectionOrder.Default.Order());
        Assert.Equal(StoreSectionOrder.Default.Count, StoreSectionOrder.Default.Distinct().Count());
    }

    [Fact]
    public void Items_sort_by_section_then_by_name_with_the_given_comparer()
    {
        var items = new[]
        {
            R("Ris", StoreSection.DryGoods),
            R("Melk", StoreSection.Dairy),
            R("Batterier", StoreSection.Other),
            R("Tomat", StoreSection.FruitAndVegetables),
            R("Agurk", StoreSection.FruitAndVegetables),
            R("Fiskepinner", StoreSection.Frozen),
            R("Laks", StoreSection.MeatFishAndCheese),
            R("Egg", StoreSection.Dairy),
        };

        var sorted = StoreSectionOrder.Sort(items, i => i.Section, i => i.Name, StringComparer.OrdinalIgnoreCase, i => i.Id);

        Assert.Equal(["Agurk", "Tomat", "Laks", "Fiskepinner", "Egg", "Melk", "Ris", "Batterier"], sorted.Select(i => i.Name));
    }

    [Fact]
    public void Equal_section_and_name_fall_back_to_the_id_so_the_order_is_deterministic()
    {
        var low = new Row("Egg", StoreSection.Dairy, new Guid("00000000-0000-0000-0000-000000000001"));
        var high = new Row("Egg", StoreSection.Dairy, new Guid("00000000-0000-0000-0000-000000000002"));

        var sorted = StoreSectionOrder.Sort([high, low], i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id);

        Assert.Equal([low, high], sorted);
    }

    [Fact]
    public void A_custom_order_replaces_the_default_and_unnamed_sections_follow_in_default_order()
    {
        var items = new[]
        {
            R("Tomat", StoreSection.FruitAndVegetables),
            R("Melk", StoreSection.Dairy),
            R("Ris", StoreSection.DryGoods),
            R("Laks", StoreSection.MeatFishAndCheese),
        };

        var full = StoreSectionOrder.Sort(
            items, i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id,
            [StoreSection.Dairy, StoreSection.DryGoods, StoreSection.FruitAndVegetables, StoreSection.MeatFishAndCheese]);
        var partial = StoreSectionOrder.Sort(
            items, i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id, [StoreSection.Dairy]);

        Assert.Equal(["Melk", "Ris", "Tomat", "Laks"], full.Select(i => i.Name));
        Assert.Equal(["Melk", "Tomat", "Laks", "Ris"], partial.Select(i => i.Name));
    }

    [Fact]
    public void An_undefined_section_sorts_with_Other_last()
    {
        Assert.Equal(StoreSectionOrder.Rank(StoreSection.Other), StoreSectionOrder.Rank((StoreSection)99));
    }

    [Fact]
    public void Section_names_parse_strictly()
    {
        Assert.True(StoreSections.TryParse("dairy", out var section));
        Assert.Equal(StoreSection.Dairy, section);
        Assert.False(StoreSections.TryParse("4", out _));
        Assert.False(StoreSections.TryParse(" Dairy", out _));
        Assert.False(StoreSections.TryParse(null, out _));
    }

    [Fact]
    public void Every_seeded_ingredient_has_a_defined_section_and_only_water_is_Other()
    {
        Assert.All(IngredientSeed.All, r => Assert.True(Enum.IsDefined(r.Section), r.Key));
        Assert.Equal(["vann"], IngredientSeed.All.Where(r => r.Section == StoreSection.Other).Select(r => r.Key));
    }

    [Theory]
    [InlineData("tomat", StoreSection.FruitAndVegetables)]
    [InlineData("basilikum", StoreSection.FruitAndVegetables)]
    [InlineData("kyllingfilet", StoreSection.MeatFishAndCheese)]
    [InlineData("parmesan", StoreSection.MeatFishAndCheese)]
    [InlineData("fiskepinner", StoreSection.Frozen)]
    [InlineData("erter", StoreSection.Frozen)]
    [InlineData("melk", StoreSection.Dairy)]
    [InlineData("egg", StoreSection.Dairy)]
    [InlineData("romme", StoreSection.Dairy)]
    [InlineData("smor", StoreSection.Dairy)]
    [InlineData("ris", StoreSection.DryGoods)]
    [InlineData("brod", StoreSection.DryGoods)]
    [InlineData("hvetemel", StoreSection.DryGoods)]
    [InlineData("salt", StoreSection.DryGoods)]
    [InlineData("olivenolje", StoreSection.DryGoods)]
    public void Seed_sections_follow_the_agreed_layout(string key, StoreSection expected) =>
        Assert.Equal(expected, IngredientSeed.All.Single(r => r.Key == key).Section);

    [Fact]
    public void An_own_ingredient_defaults_to_Other_and_can_change_section()
    {
        var ingredient = new Ingredient(Guid.NewGuid(), "Surdeig", false, UnitDimension.Mass);
        Assert.Equal(StoreSection.Other, ingredient.Section);

        ingredient.ChangeSection(StoreSection.DryGoods);

        Assert.Equal(StoreSection.DryGoods, ingredient.Section);
        Assert.Throws<ArgumentOutOfRangeException>(() => ingredient.ChangeSection((StoreSection)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ingredient(Guid.NewGuid(), "x", false, UnitDimension.Mass, (StoreSection)99));
    }
}
