using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class PreferredOdaProductTests
{
    [Fact]
    public void A_pick_holds_the_ingredient_and_the_oda_product_id_only()
    {
        var ingredient = Guid.NewGuid();

        var pick = new PreferredOdaProduct(ingredient, 12345);

        Assert.Equal(ingredient, pick.IngredientId);
        Assert.Equal(12345, pick.OdaProductId);
        Assert.Equal(500, PreferredOdaProduct.MaxPerHousehold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void A_product_id_must_be_above_zero(long id)
    {
        Assert.False(PreferredOdaProduct.IsValidProductId(id));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreferredOdaProduct(Guid.NewGuid(), id));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreferredOdaProduct(Guid.NewGuid(), 1).Replace(id));
    }

    [Fact]
    public void An_ingredient_is_required_and_a_new_pick_replaces_the_old()
    {
        Assert.Throws<ArgumentException>(() => new PreferredOdaProduct(Guid.Empty, 1));
        var pick = new PreferredOdaProduct(Guid.NewGuid(), 1);

        pick.Replace(2);
        var target = Guid.NewGuid();
        pick.ChangeIngredient(target);

        Assert.Equal(2, pick.OdaProductId);
        Assert.Equal(target, pick.IngredientId);
        Assert.Throws<ArgumentException>(() => pick.ChangeIngredient(Guid.Empty));
    }
}
