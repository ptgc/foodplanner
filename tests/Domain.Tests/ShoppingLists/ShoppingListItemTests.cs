using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class ShoppingListItemTests
{
    private static readonly Guid List = Guid.NewGuid();
    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly Guid User2 = Guid.NewGuid();
    private static readonly DateTimeOffset T1 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(5);

    private static ShoppingListItem Item() => ShoppingListItem.Generated(List, Guid.NewGuid(), ShoppingSection.Main);

    [Fact]
    public void Generated_items_have_an_ingredient_and_no_label_and_manual_items_the_reverse()
    {
        var generated = Item();
        var manual = ShoppingListItem.Manual(List, "  Birthday   candles ");

        Assert.False(generated.IsManual);
        Assert.Null(generated.Label);
        Assert.True(manual.IsManual);
        Assert.Null(manual.IngredientId);
        Assert.Equal("Birthday candles", manual.Label);
        Assert.Equal(ShoppingSection.Main, manual.Section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0007b")]
    public void An_invalid_label_is_refused(string? label)
    {
        Assert.Null(ShoppingListItem.NormalizeLabel(label));
        Assert.Throws<ArgumentException>(() => ShoppingListItem.Manual(List, label!));
    }

    [Fact]
    public void A_label_of_exactly_the_maximum_length_is_accepted_and_one_more_is_not()
    {
        Assert.NotNull(ShoppingListItem.NormalizeLabel(new string('x', ShoppingListItem.MaxLabelLength)));
        Assert.Null(ShoppingListItem.NormalizeLabel(new string('x', ShoppingListItem.MaxLabelLength + 1)));
    }

    [Fact]
    public void Checking_records_who_and_when_and_checking_again_keeps_the_first_checker()
    {
        var item = Item();

        Assert.True(item.SetChecked(true, User1, T1));
        Assert.False(item.SetChecked(true, User2, T2));

        Assert.True(item.Checked);
        Assert.Equal(User1, item.CheckedByUserId);
        Assert.Equal(T1, item.CheckedAt);
    }

    [Fact]
    public void Unchecking_clears_everything_and_is_a_no_op_when_already_unchecked()
    {
        var item = Item();
        Assert.False(item.SetChecked(false, User1, T1));
        item.SetChecked(true, User1, T1);

        Assert.True(item.SetChecked(false, User2, T2));

        Assert.False(item.Checked);
        Assert.Null(item.CheckedByUserId);
        Assert.Null(item.CheckedAt);
        Assert.False(item.SetChecked(false, User2, T2));
    }

    [Fact]
    public void A_quantity_change_flags_only_a_checked_item_and_checking_again_clears_the_flag_and_keeps_the_checker()
    {
        var unchecked_ = Item();
        unchecked_.QuantitiesChanged();
        Assert.False(unchecked_.QuantityChangedSinceChecked);

        var item = Item();
        item.SetChecked(true, User1, T1);
        item.QuantitiesChanged();
        Assert.True(item.QuantityChangedSinceChecked);
        Assert.True(item.Checked);

        Assert.True(item.SetChecked(true, User2, T2));

        Assert.False(item.QuantityChangedSinceChecked);
        Assert.Equal(User1, item.CheckedByUserId);
        Assert.Equal(T1, item.CheckedAt);
        Assert.False(item.SetChecked(true, User2, T2));
    }

    [Fact]
    public void Unchecking_a_flagged_item_clears_the_flag()
    {
        var item = Item();
        item.SetChecked(true, User1, T1);
        item.QuantitiesChanged();

        Assert.True(item.SetChecked(false, User1, T2));

        Assert.False(item.QuantityChangedSinceChecked);
        Assert.False(item.Checked);
    }

    [Fact]
    public void Moving_to_another_section_starts_the_item_over()
    {
        var item = Item();
        item.SetChecked(true, User1, T1);

        item.MoveTo(ShoppingSection.CheckYouHave);

        Assert.Equal(ShoppingSection.CheckYouHave, item.Section);
        Assert.False(item.Checked);
        Assert.Null(item.CheckedByUserId);
    }

    [Fact]
    public void Quantity_lines_need_amount_and_unit_together_and_a_positive_amount()
    {
        var item = Guid.NewGuid();
        var unit = Guid.NewGuid();

        _ = new ShoppingListItemQuantity(item, 0, 2.5m, unit);
        _ = new ShoppingListItemQuantity(item, 1, null, null); // to taste
        Assert.Throws<ArgumentException>(() => new ShoppingListItemQuantity(item, 0, 1m, null));
        Assert.Throws<ArgumentException>(() => new ShoppingListItemQuantity(item, 0, null, unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShoppingListItemQuantity(item, 0, 0m, unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShoppingListItemQuantity(item, -1, 1m, unit));
    }

    [Fact]
    public void A_list_must_start_on_a_Monday()
    {
        _ = new ShoppingList(new DateOnly(2026, 10, 26));
        Assert.Throws<ArgumentException>(() => new ShoppingList(new DateOnly(2026, 10, 27)));
    }
}
