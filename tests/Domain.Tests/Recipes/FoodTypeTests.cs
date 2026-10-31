using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Recipes;

public class FoodTypeTests
{
    private static FoodTypeLine MainLine(ProteinClass protein) => new(protein, IngredientPurpose.Main, false);

    private static FoodType Derive(params ProteinClass[] main) => FoodTypeDeriver.Derive(main.Select(MainLine));

    [Fact]
    public void No_lines_is_unknown() => Assert.Equal(FoodType.Unknown, FoodTypeDeriver.Derive([]));

    [Theory]
    [InlineData(ProteinClass.Chicken, FoodType.Chicken)]
    [InlineData(ProteinClass.Beef, FoodType.Beef)]
    [InlineData(ProteinClass.Pork, FoodType.Pork)]
    [InlineData(ProteinClass.Lamb, FoodType.Lamb)]
    [InlineData(ProteinClass.Meat, FoodType.MixedMeat)]
    [InlineData(ProteinClass.Fish, FoodType.Fish)]
    [InlineData(ProteinClass.Seafood, FoodType.Seafood)]
    [InlineData(ProteinClass.AnimalProduct, FoodType.Vegetarian)]
    [InlineData(ProteinClass.None, FoodType.Vegan)]
    [InlineData(ProteinClass.Unknown, FoodType.Unknown)]
    public void One_ingredient_gives_its_type(ProteinClass protein, FoodType expected) =>
        Assert.Equal(expected, Derive(protein, ProteinClass.None));

    [Fact]
    public void Meat_beats_fish_and_dairy()
    {
        Assert.Equal(FoodType.Pork, Derive(ProteinClass.Pork, ProteinClass.Fish, ProteinClass.AnimalProduct));
        Assert.Equal(FoodType.Beef, Derive(ProteinClass.None, ProteinClass.AnimalProduct, ProteinClass.Beef));
    }

    [Fact]
    public void Fish_beats_seafood_and_dairy_and_seafood_beats_dairy()
    {
        Assert.Equal(FoodType.Fish, Derive(ProteinClass.Seafood, ProteinClass.Fish, ProteinClass.AnimalProduct));
        Assert.Equal(FoodType.Seafood, Derive(ProteinClass.Seafood, ProteinClass.AnimalProduct));
    }

    [Fact]
    public void Two_different_meats_are_mixed()
    {
        Assert.Equal(FoodType.MixedMeat, Derive(ProteinClass.Beef, ProteinClass.Pork));
        Assert.Equal(FoodType.MixedMeat, Derive(ProteinClass.Chicken, ProteinClass.Lamb, ProteinClass.Beef));
    }

    [Fact]
    public void The_same_meat_twice_is_that_meat() =>
        Assert.Equal(FoodType.Chicken, Derive(ProteinClass.Chicken, ProteinClass.Chicken));

    [Fact]
    public void Unspecified_meat_next_to_one_specific_kind_is_that_kind() =>
        Assert.Equal(FoodType.Pork, Derive(ProteinClass.Meat, ProteinClass.Pork));

    [Fact]
    public void An_unclassified_ingredient_blocks_vegetarian_and_vegan_but_not_meat_or_fish()
    {
        Assert.Equal(FoodType.Unknown, Derive(ProteinClass.Unknown, ProteinClass.AnimalProduct));
        Assert.Equal(FoodType.Unknown, Derive(ProteinClass.Unknown, ProteinClass.None));
        Assert.Equal(FoodType.Beef, Derive(ProteinClass.Unknown, ProteinClass.Beef));
        Assert.Equal(FoodType.Fish, Derive(ProteinClass.Unknown, ProteinClass.Fish));
    }

    [Theory]
    [InlineData(IngredientPurpose.Frying)]
    [InlineData(IngredientPurpose.Serving)]
    [InlineData(IngredientPurpose.Garnish)]
    [InlineData(IngredientPurpose.Seasoning)]
    public void Only_main_purpose_lines_count(IngredientPurpose purpose)
    {
        var lines = new[]
        {
            MainLine(ProteinClass.None),
            new FoodTypeLine(ProteinClass.Pork, purpose, false),
        };
        Assert.Equal(FoodType.Vegan, FoodTypeDeriver.Derive(lines));
    }

    [Fact]
    public void Optional_lines_do_not_count()
    {
        var lines = new[]
        {
            MainLine(ProteinClass.AnimalProduct),
            new FoodTypeLine(ProteinClass.Chicken, IngredientPurpose.Main, true),
        };
        Assert.Equal(FoodType.Vegetarian, FoodTypeDeriver.Derive(lines));
    }

    [Fact]
    public void Only_non_main_or_optional_lines_is_unknown()
    {
        var lines = new[]
        {
            new FoodTypeLine(ProteinClass.Beef, IngredientPurpose.Serving, false),
            new FoodTypeLine(ProteinClass.Beef, IngredientPurpose.Main, true),
        };
        Assert.Equal(FoodType.Unknown, FoodTypeDeriver.Derive(lines));
    }

    [Fact]
    public void A_manual_type_wins_and_sticks()
    {
        var recipe = new Recipe("Gryte", null, 4, null, null);
        recipe.SetFoodType(FoodType.Lamb, FoodType.Vegan);
        Assert.Equal(FoodType.Lamb, recipe.FoodType);
        Assert.True(recipe.FoodTypeIsManual);

        Assert.False(recipe.ApplyDerivedFoodType(FoodType.Beef));
        Assert.Equal(FoodType.Lamb, recipe.FoodType);
    }

    [Fact]
    public void Automatic_follows_the_derived_type()
    {
        var recipe = new Recipe("Gryte", null, 4, null, null);
        recipe.SetFoodType(null, FoodType.Fish);
        Assert.Equal(FoodType.Fish, recipe.FoodType);
        Assert.False(recipe.FoodTypeIsManual);

        Assert.True(recipe.ApplyDerivedFoodType(FoodType.Vegetarian));
        Assert.Equal(FoodType.Vegetarian, recipe.FoodType);
        Assert.False(recipe.ApplyDerivedFoodType(FoodType.Vegetarian));

        recipe.SetFoodType(FoodType.Beef, FoodType.Vegetarian);
        recipe.SetFoodType(null, FoodType.Vegan); // back to automatic
        Assert.Equal(FoodType.Vegan, recipe.FoodType);
        Assert.False(recipe.FoodTypeIsManual);
    }

    [Fact]
    public void An_undefined_manual_type_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Recipe("Gryte", null, 4, null, null).SetFoodType((FoodType)99, FoodType.Unknown));

    [Theory]
    [InlineData("Fish", FoodType.Fish)]
    [InlineData("mixedmeat", FoodType.MixedMeat)]
    [InlineData("VEGAN", FoodType.Vegan)]
    public void Parse_is_by_name_only(string value, FoodType expected)
    {
        Assert.True(FoodTypes.TryParse(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Poultry")]
    public void Parse_rejects_numbers_and_unknown_names(string? value) => Assert.False(FoodTypes.TryParse(value, out _));

    [Fact]
    public void Every_seeded_ingredient_has_a_class_and_the_meat_and_fish_are_right()
    {
        // None is the plant-based default; Unknown must never be seeded (it means "an adult has not classified it yet").
        Assert.DoesNotContain(IngredientSeed.All, r => r.Protein == ProteinClass.Unknown);
        ProteinClass Of(string key) => IngredientSeed.All.Single(r => r.Key == key).Protein;
        Assert.Equal(ProteinClass.Chicken, Of("kyllingfilet"));
        Assert.Equal(ProteinClass.Fish, Of("laks"));
        Assert.Equal(ProteinClass.Seafood, Of("reker"));
        Assert.Equal(ProteinClass.Meat, Of("kjottdeig"));
        Assert.Equal(ProteinClass.AnimalProduct, Of("melk"));
        Assert.Equal(ProteinClass.AnimalProduct, Of("egg"));
        Assert.Equal(ProteinClass.None, Of("potet"));
        Assert.Equal(ProteinClass.None, Of("olivenolje"));
    }

    [Fact]
    public void An_own_ingredient_starts_unclassified_and_can_be_classified()
    {
        var ingredient = new Ingredient(Guid.Empty, "Reinsdyr", false, UnitDimension.Mass);
        Assert.Equal(ProteinClass.Unknown, ingredient.ProteinClass);
        ingredient.ChangeProteinClass(ProteinClass.Meat);
        Assert.Equal(ProteinClass.Meat, ingredient.ProteinClass);
        Assert.Throws<ArgumentOutOfRangeException>(() => ingredient.ChangeProteinClass((ProteinClass)99));
    }
}
