using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class PantryMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Salt = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Sea = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid Anna = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Bo = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Fact]
    public void The_newer_confirmation_wins_and_the_later_expiry_is_kept()
    {
        var target = new PantryItem(Salt, Anna, Now, Now.AddDays(10));
        var source = new PantryItem(Sea, Bo, Now.AddDays(1), Now.AddDays(5));

        target.Absorb(source);

        Assert.Equal(Bo, target.ConfirmedByUserId);
        Assert.Equal(Now.AddDays(1), target.ConfirmedAt);
        Assert.Equal(Now.AddDays(10), target.ExpiresAt);
        Assert.Equal(Salt, target.IngredientId);
    }

    [Fact]
    public void No_expiry_counts_as_the_latest_in_both_directions()
    {
        var withExpiry = new PantryItem(Salt, Anna, Now, Now.AddDays(3));
        withExpiry.Absorb(new PantryItem(Sea, Bo, Now.AddHours(-1), null));
        Assert.Null(withExpiry.ExpiresAt);
        Assert.Equal(Anna, withExpiry.ConfirmedByUserId); // the target's confirmation is the newer one

        var noExpiry = new PantryItem(Salt, Anna, Now, null);
        noExpiry.Absorb(new PantryItem(Sea, Bo, Now.AddDays(1), Now.AddDays(2)));
        Assert.Null(noExpiry.ExpiresAt);
        Assert.Equal(Bo, noExpiry.ConfirmedByUserId);
    }

    [Fact]
    public void Change_ingredient_rejects_an_empty_id()
    {
        var item = new PantryItem(Salt, Anna, Now, null);
        Assert.Throws<ArgumentException>(() => item.ChangeIngredient(Guid.Empty));
        item.ChangeIngredient(Sea);
        Assert.Equal(Sea, item.IngredientId);
    }
}
