using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class GroceryLabelTests
{
    [Theory]
    [InlineData("Melk", "melk")]
    [InlineData("  MELK  ", "melk")]
    [InlineData("hvit   ost", "hvit ost")]
    [InlineData("Hvit\tOST", "hvit ost")]
    [InlineData("melk!", "melk")]
    [InlineData("  ", "")]
    [InlineData("-", "")]
    [InlineData(null, "")]
    public void Case_whitespace_and_punctuation_are_ignored(string? input, string expected) =>
        Assert.Equal(expected, GroceryLabel.Normalize(input));

    [Theory]
    [InlineData("Crème fraîche", "crem fraich")]
    [InlineData("Jalapeño", "jalapeno")]
    [InlineData("Müsli", "musli")]
    [InlineData("Köttbullar", "kottbullar")]
    public void Foreign_diacritics_are_dropped(string input, string expected) =>
        Assert.Equal(expected, GroceryLabel.Normalize(input));

    [Fact]
    public void Decomposed_and_precomposed_spellings_are_the_same()
    {
        Assert.Equal(GroceryLabel.Normalize("Café"), GroceryLabel.Normalize("Café"));
        Assert.Equal(GroceryLabel.Normalize("Pålegg"), GroceryLabel.Normalize("Pålegg"));
    }

    [Theory]
    [InlineData("ål", "al")]
    [InlineData("høne", "hone")]
    [InlineData("hære", "hare")]
    [InlineData("bær", "bar")]
    [InlineData("rømme", "romme")]
    [InlineData("Pålegg", "Palegg")]
    public void The_norwegian_letters_never_merge_with_a_o_or_e_words(string norwegian, string plain) =>
        Assert.NotEqual(GroceryLabel.Normalize(plain), GroceryLabel.Normalize(norwegian));

    [Theory]
    [InlineData("Pålegg", "pålegg")]
    [InlineData("Brød", "brød")]
    [InlineData("Smør", "smør")]
    [InlineData("Ærter", "ært")]
    public void The_norwegian_letters_stay_themselves(string input, string expected) =>
        Assert.Equal(expected, GroceryLabel.Normalize(input));

    [Theory]
    [InlineData("ost", "oster")]
    [InlineData("tomat", "tomater")]
    [InlineData("banan", "Bananer")]
    [InlineData("eple", "epler")]
    [InlineData("pære", "pærer")]
    [InlineData("apple", "apples")]
    [InlineData("tomato", "tomatoes")]
    [InlineData("berry", "berries")]
    [InlineData("potet", "poteter")]
    [InlineData("cheese", "cheeses")]
    [InlineData("egg", "Egg ")]
    public void A_simple_plural_meets_its_singular(string a, string b) =>
        Assert.Equal(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    [Theory]
    [InlineData("melk", "melon")]
    [InlineData("mel", "melk")]
    [InlineData("ris", "rev")]
    [InlineData("ost", "ose")]
    [InlineData("hummus", "humm")]
    [InlineData("lam", "lamme")]
    [InlineData("kake", "kaker mel")]
    [InlineData("brød", "bro")]
    public void Different_things_do_not_merge(string a, string b) =>
        Assert.NotEqual(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    [Fact]
    public void Words_are_stemmed_one_by_one()
    {
        Assert.Equal(GroceryLabel.Normalize("hvit ost"), GroceryLabel.Normalize("Hvite Oster"));
        Assert.NotEqual(GroceryLabel.Normalize("hvit ost"), GroceryLabel.Normalize("ost hvit"));
    }

    [Fact]
    public void Short_words_and_words_ending_in_s_u_i_are_left_alone()
    {
        Assert.Equal("ris", GroceryLabel.Normalize("ris"));
        Assert.Equal("hummus", GroceryLabel.Normalize("hummus"));
        Assert.Equal("couscous", GroceryLabel.Normalize("couscous"));
        Assert.Equal("ler", GroceryLabel.Normalize("ler"));
        Assert.Equal("kiwi", GroceryLabel.Normalize("kiwi"));
    }

    [Fact]
    public void A_key_is_never_longer_than_the_label()
    {
        var label = new string('x', 100) + "er";
        Assert.True(GroceryLabel.Normalize(label).Length <= label.Length);
    }
}
