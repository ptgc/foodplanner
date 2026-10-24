using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Recipes;

public sealed class RecipeTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid IngredientId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();

    [Fact]
    public void Total_minutes_is_prep_plus_cook_or_null_when_neither_is_set()
    {
        Assert.Null(new Recipe("Pasta", null, 4, null, null).TotalMinutes);
        Assert.Equal(10, new Recipe("Pasta", null, 4, 10, null).TotalMinutes);
        Assert.Equal(25, new Recipe("Pasta", null, 4, null, 25).TotalMinutes);
        Assert.Equal(35, new Recipe("Pasta", null, 4, 10, 25).TotalMinutes);
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData("  ", 4)]
    [InlineData("ok", 0)]
    [InlineData("ok", 101)]
    public void Invalid_title_or_servings_are_refused(string title, int servings) =>
        Assert.ThrowsAny<ArgumentException>(() => new Recipe(title, null, servings, null, null));

    [Fact]
    public void Limits_are_inclusive()
    {
        var recipe = new Recipe(new string('x', Recipe.MaxTitleLength), new string('y', Recipe.MaxDescriptionLength), Recipe.MaxServings, 0, Recipe.MaxMinutes);

        Assert.Equal(Recipe.MaxMinutes, recipe.TotalMinutes);
        Assert.ThrowsAny<ArgumentException>(() => new Recipe(new string('x', Recipe.MaxTitleLength + 1), null, 1, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", new string('y', Recipe.MaxDescriptionLength + 1), 1, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", null, 1, -1, null));
        Assert.ThrowsAny<ArgumentException>(() => new Recipe("ok", null, 1, null, Recipe.MaxMinutes + 1));
    }

    [Fact]
    public void Update_replaces_the_fields_and_validates_like_the_constructor()
    {
        var recipe = new Recipe("Pasta", null, 4, 1, 2);

        recipe.Update("Lasagne", "Layers", 6, null, 45);

        Assert.Equal(("Lasagne", "Layers", 6, null, 45), (recipe.Title, recipe.Description, recipe.Servings, recipe.PrepMinutes, recipe.CookMinutes));
        Assert.ThrowsAny<ArgumentException>(() => recipe.Update("", null, 6, null, null));
        Assert.Equal("Lasagne", recipe.Title);
    }

    [Fact]
    public void A_step_needs_a_recipe_and_text()
    {
        Assert.Equal("Stir", new RecipeStep(RecipeId, 0, "Stir").Text);
        Assert.Throws<ArgumentException>(() => new RecipeStep(Guid.Empty, 0, "Stir"));
        Assert.Throws<ArgumentException>(() => new RecipeStep(RecipeId, 0, " "));
        Assert.Throws<ArgumentException>(() => new RecipeStep(RecipeId, 0, new string('x', RecipeStep.MaxTextLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeStep(RecipeId, -1, "Stir"));
    }

    [Fact]
    public void An_ingredient_line_is_structured_and_to_taste_has_no_quantity_and_no_unit()
    {
        var measured = new RecipeIngredient(RecipeId, 0, IngredientId, 1.5m, UnitId, "chopped");
        var toTaste = new RecipeIngredient(RecipeId, 1, IngredientId, null, null, null);

        Assert.Equal((1.5m, UnitId, "chopped"), (measured.Quantity, measured.UnitId, measured.Note));
        Assert.Equal((null, null), (toTaste.Quantity, toTaste.UnitId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000.01)]
    [InlineData(0.00001)]
    public void Quantity_must_be_positive_bounded_and_at_most_four_decimals(double quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, (decimal)quantity, UnitId, null));

    [Fact]
    public void Quantity_and_unit_go_together_and_ids_are_required()
    {
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, null, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, null, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, Guid.Empty, 1m, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(Guid.Empty, 0, IngredientId, 1m, UnitId, null));
        Assert.Throws<ArgumentException>(() => new RecipeIngredient(RecipeId, 0, IngredientId, 1m, UnitId, new string('x', RecipeIngredient.MaxNoteLength + 1)));
        Assert.Equal(100000m, new RecipeIngredient(RecipeId, 0, IngredientId, 100000m, UnitId, null).Quantity);
    }
}
