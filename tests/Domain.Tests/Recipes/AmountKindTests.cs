using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.Recipes;

public class AmountKindTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid IngredientId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();

    [Theory]
    [InlineData(AmountKind.Measured, 1.5, true, null)]
    [InlineData(AmountKind.Measured, null, true, AmountProblem.QuantityRequired)]
    [InlineData(AmountKind.Measured, 1.5, false, AmountProblem.UnitRequired)]
    [InlineData(AmountKind.Measured, null, false, AmountProblem.QuantityRequired)]
    [InlineData(AmountKind.ToTaste, null, false, null)]
    [InlineData(AmountKind.ToTaste, 1.0, true, AmountProblem.QuantityForbidden)]
    [InlineData(AmountKind.ToTaste, null, true, AmountProblem.UnitForbidden)]
    [InlineData(AmountKind.AsNeeded, null, false, null)]
    [InlineData(AmountKind.AsNeeded, 2.0, true, AmountProblem.QuantityForbidden)]
    [InlineData(AmountKind.AsNeeded, null, true, AmountProblem.UnitForbidden)]
    [InlineData((AmountKind)99, null, false, AmountProblem.UnknownKind)]
    public void Measured_needs_quantity_and_unit_and_the_others_forbid_them(AmountKind kind, double? quantity, bool hasUnit, AmountProblem? expected) =>
        Assert.Equal(expected, AmountRules.Check(kind, (decimal?)quantity, hasUnit ? UnitId : null));

    [Fact]
    public void The_kind_is_inferred_when_it_is_not_given()
    {
        Assert.Equal(AmountKind.Measured, new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, null).AmountKind);
        var toTaste = new RecipeIngredient(RecipeId, 0, IngredientId, null, null, null);
        Assert.Equal(AmountKind.ToTaste, toTaste.AmountKind);
        Assert.Equal(IngredientPurpose.Main, toTaste.Purpose);
        Assert.False(toTaste.IsOptional);
    }

    [Fact]
    public void A_line_keeps_kind_purpose_and_optional()
    {
        var line = new RecipeIngredient(RecipeId, 2, IngredientId, null, null, "eller margarin", AmountKind.AsNeeded, IngredientPurpose.Frying, isOptional: true);

        Assert.Equal(AmountKind.AsNeeded, line.AmountKind);
        Assert.Equal(IngredientPurpose.Frying, line.Purpose);
        Assert.True(line.IsOptional);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitId);
    }

    [Fact]
    public void Invalid_combinations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, null, AmountKind.ToTaste));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, null, null, null, AmountKind.Measured));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, null, AmountKind.AsNeeded));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, null, purpose: (IngredientPurpose)99));
    }

    [Fact]
    public void Only_a_measured_line_is_scaled()
    {
        var catalog = UnitCatalog.Seed;
        var amount = new Quantity(2m, catalog.Get("dl"));
        var noAmount = new Quantity(null, catalog.Get("stk"));

        Assert.Equal(6m, RecipeScaling.Scale(AmountKind.Measured, amount, 4, 12).Amount);
        Assert.Null(RecipeScaling.Scale(AmountKind.ToTaste, noAmount, 4, 12).Amount);
        Assert.Null(RecipeScaling.Scale(AmountKind.AsNeeded, noAmount, 4, 100).Amount);
        // Even a stray amount on a non-measured line is never multiplied.
        Assert.Equal(2m, RecipeScaling.Scale(AmountKind.ToTaste, amount, 4, 12).Amount);
    }

    [Fact]
    public void An_alternative_needs_a_line_and_an_ingredient()
    {
        var alternative = new RecipeIngredientAlternative(Guid.NewGuid(), 0, IngredientId);

        Assert.Equal(IngredientId, alternative.IngredientId);
        Assert.Throws<ArgumentException>(() => new RecipeIngredientAlternative(Guid.Empty, 0, IngredientId));
        Assert.Throws<ArgumentException>(() => new RecipeIngredientAlternative(Guid.NewGuid(), 0, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeIngredientAlternative(Guid.NewGuid(), -1, IngredientId));
    }

    [Fact]
    public void Shopping_alternatives_are_the_union_without_the_item_itself()
    {
        var self = Guid.NewGuid();
        var oil = Guid.NewGuid();
        var margarine = Guid.NewGuid();

        var result = ShoppingAlternatives.For(self, [[oil, margarine], [self, oil], []]);

        Assert.Equal([oil, margarine], result);
        Assert.Empty(ShoppingAlternatives.For(self, []));
    }
}
