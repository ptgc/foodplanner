using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.ShoppingLists;

/// <summary>T-087: a store's section order, ordering by a custom order with fallback, and the chosen store on a membership.</summary>
public class StoreTests
{
    private static readonly StoreSection[] DairyFirst =
        [StoreSection.Dairy, StoreSection.FruitAndVegetables, StoreSection.MeatFishAndCheese, StoreSection.Frozen, StoreSection.DryGoods, StoreSection.Other];

    private sealed record Row(string Name, StoreSection Section, Guid Id);

    private static Row R(string name, StoreSection section) => new(name, section, Guid.NewGuid());

    [Fact]
    public void An_order_must_name_every_section_exactly_once()
    {
        Assert.True(StoreSectionOrder.IsComplete(StoreSectionOrder.Default));
        Assert.True(StoreSectionOrder.IsComplete(DairyFirst));
        Assert.False(StoreSectionOrder.IsComplete(null));
        Assert.False(StoreSectionOrder.IsComplete([]));
        Assert.False(StoreSectionOrder.IsComplete([.. StoreSectionOrder.Default.Take(5)])); // one missing
        Assert.False(StoreSectionOrder.IsComplete([.. StoreSectionOrder.Default.Take(5), StoreSection.Dairy])); // one twice
        Assert.False(StoreSectionOrder.IsComplete([.. StoreSectionOrder.Default.Take(5), (StoreSection)99])); // one unknown
        Assert.False(StoreSectionOrder.IsComplete([.. StoreSectionOrder.Default, StoreSection.Other])); // too many
    }

    [Fact]
    public void The_effective_order_is_the_named_sections_then_the_rest_in_default_order()
    {
        Assert.Same(StoreSectionOrder.Default, StoreSectionOrder.Effective(null));
        Assert.Equal(DairyFirst, StoreSectionOrder.Effective(DairyFirst));
        Assert.Equal(
            [StoreSection.Dairy, StoreSection.DryGoods, StoreSection.FruitAndVegetables, StoreSection.MeatFishAndCheese, StoreSection.Frozen, StoreSection.Other],
            StoreSectionOrder.Effective([StoreSection.Dairy, StoreSection.DryGoods, StoreSection.Dairy, (StoreSection)99]));
        Assert.Equal(StoreSectionOrder.Default, StoreSectionOrder.Effective([]));
    }

    [Fact]
    public void Sorting_by_a_custom_order_matches_the_effective_order_and_null_is_the_fixed_order()
    {
        var items = new[]
        {
            R("Brød", StoreSection.DryGoods),
            R("Melk", StoreSection.Dairy),
            R("Tomat", StoreSection.FruitAndVegetables),
            R("Fisk", StoreSection.Frozen),
            R("Laks", StoreSection.MeatFishAndCheese),
            R("Vann", StoreSection.Other),
        };

        var custom = StoreSectionOrder.Sort(items, i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id, DairyFirst);
        var fallback = StoreSectionOrder.Sort(items, i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id, null);

        Assert.Equal(["Melk", "Tomat", "Laks", "Fisk", "Brød", "Vann"], custom.Select(i => i.Name));
        Assert.Equal(["Tomat", "Laks", "Fisk", "Melk", "Brød", "Vann"], fallback.Select(i => i.Name));
        Assert.Equal(DairyFirst, items.OrderBy(i => StoreSectionOrder.Rank(i.Section, DairyFirst)).Select(i => i.Section));
    }

    [Fact]
    public void Items_of_one_section_keep_the_name_order_under_a_custom_order()
    {
        var items = new[] { R("Yoghurt", StoreSection.Dairy), R("Melk", StoreSection.Dairy), R("Tomat", StoreSection.FruitAndVegetables) };

        var sorted = StoreSectionOrder.Sort(items, i => i.Section, i => i.Name, StringComparer.Ordinal, i => i.Id, DairyFirst);

        Assert.Equal(["Melk", "Yoghurt", "Tomat"], sorted.Select(i => i.Name));
    }

    [Fact]
    public void A_new_store_defaults_to_the_fixed_order_and_keeps_a_given_order()
    {
        Assert.Equal(StoreSectionOrder.Default, new Store("Rema").SectionOrder);
        Assert.Equal(DairyFirst, new Store("Rema", DairyFirst).SectionOrder);
    }

    [Fact]
    public void The_order_round_trips_through_its_persisted_text()
    {
        var store = new Store("Rema", DairyFirst);

        Assert.Equal("Dairy,FruitAndVegetables,MeatFishAndCheese,Frozen,DryGoods,Other", store.SectionOrderText);
    }

    [Fact]
    public void An_incomplete_order_is_refused()
    {
        var store = new Store("Rema");

        Assert.Throws<ArgumentException>(() => new Store("Rema", [StoreSection.Dairy]));
        Assert.Throws<ArgumentException>(() => store.SetSectionOrder([.. DairyFirst.Take(5)]));
        Assert.Equal(StoreSectionOrder.Default, store.SectionOrder);
    }

    [Theory]
    [InlineData("Rema", "Rema")]
    [InlineData("  Kiwi   sentrum ", "Kiwi sentrum")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("a\tb\u0001", null)]
    public void Names_are_trimmed_and_collapsed(string? input, string? expected) =>
        Assert.Equal(expected, Store.NormalizeName(input));

    [Fact]
    public void A_name_is_one_to_one_hundred_characters()
    {
        Assert.NotNull(Store.NormalizeName(new string('x', Store.MaxNameLength)));
        Assert.Null(Store.NormalizeName(new string('x', Store.MaxNameLength + 1)));
        Assert.Throws<ArgumentException>(() => new Store(" "));
        Assert.Throws<ArgumentException>(() => new Store("Rema").Rename(new string('x', 101)));
    }

    [Fact]
    public void A_membership_remembers_a_chosen_store_and_can_clear_it()
    {
        var membership = new Membership(Guid.NewGuid(), Guid.NewGuid(), HouseholdRole.Child);
        var store = Guid.NewGuid();

        Assert.Null(membership.ChosenStoreId);
        membership.ChooseStore(store);
        Assert.Equal(store, membership.ChosenStoreId);
        membership.ChooseStore(null);
        Assert.Null(membership.ChosenStoreId);
        Assert.Throws<ArgumentException>(() => membership.ChooseStore(Guid.Empty));
    }
}
