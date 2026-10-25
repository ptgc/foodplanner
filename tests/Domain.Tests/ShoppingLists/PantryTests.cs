using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class PantryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Salt = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Flour = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Rice = Guid.Parse("00000000-0000-0000-0000-000000000009");
    private static readonly Guid User = Guid.Parse("20000000-0000-0000-0000-000000000001");

    private static GeneratedList List() => new(
        [new GeneratedItem(Rice, "Ris", [], [])],
        [new GeneratedStaple(Flour, "Hvetemel", []), new GeneratedStaple(Salt, "Salt", [])]);

    [Fact]
    public void An_active_entry_removes_the_staple_and_keeps_the_rest_in_order()
    {
        var result = PantryFilter.Apply(List(), [new PantryEntry(Salt, null)], Now);

        Assert.Equal([Flour], result.CheckYouHave.Select(s => s.IngredientId));
        Assert.Equal([Rice], result.Items.Select(i => i.IngredientId));
    }

    [Fact]
    public void An_entry_that_has_not_expired_still_hides_the_staple_and_an_expired_one_does_not()
    {
        Assert.Equal([Flour], PantryFilter.Apply(List(), [new PantryEntry(Salt, Now.AddDays(1))], Now).CheckYouHave.Select(s => s.IngredientId));
        Assert.Equal(2, PantryFilter.Apply(List(), [new PantryEntry(Salt, Now.AddDays(1))], Now.AddDays(2)).CheckYouHave.Count);
        Assert.Equal(2, PantryFilter.Apply(List(), [new PantryEntry(Salt, Now)], Now).CheckYouHave.Count); // expires at the instant
    }

    [Fact]
    public void The_pantry_never_filters_the_main_list()
    {
        var result = PantryFilter.Apply(List(), [new PantryEntry(Rice, null)], Now);

        Assert.Single(result.Items);
        Assert.Equal(2, result.CheckYouHave.Count);
    }

    [Fact]
    public void An_empty_pantry_changes_nothing()
    {
        var list = List();

        Assert.Same(list, PantryFilter.Apply(list, [], Now));
    }

    [Fact]
    public void Confirming_again_replaces_the_expiry_and_the_user()
    {
        var item = new PantryItem(Salt, User, Now, Now.AddDays(10));
        var other = Guid.NewGuid();

        item.Confirm(other, Now.AddDays(1), null);

        Assert.Equal(other, item.ConfirmedByUserId);
        Assert.Equal(Now.AddDays(1), item.ConfirmedAt);
        Assert.Null(item.ExpiresAt);
        Assert.True(item.IsActive(Now.AddYears(50)));
    }

    [Fact]
    public void Expiry_must_be_after_the_confirmation_and_ids_are_required()
    {
        Assert.Throws<ArgumentException>(() => new PantryItem(Salt, User, Now, Now));
        Assert.Throws<ArgumentException>(() => new PantryItem(Salt, User, Now, Now.AddSeconds(-1)));
        Assert.Throws<ArgumentException>(() => new PantryItem(Guid.Empty, User, Now, null));
        Assert.Throws<ArgumentException>(() => new PantryItem(Salt, Guid.Empty, Now, null));
    }

    [Fact]
    public void An_item_is_active_until_its_expiry()
    {
        var item = new PantryItem(Salt, User, Now, Now.AddDays(1));

        Assert.True(item.IsActive(Now.AddHours(23)));
        Assert.False(item.IsActive(Now.AddDays(1)));
    }
}
