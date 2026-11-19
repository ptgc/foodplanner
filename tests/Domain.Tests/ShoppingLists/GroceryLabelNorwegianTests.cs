using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

/// <summary>Verifier probes (T-110): real Norwegian grocery names, what merges and what stays apart.</summary>
public class GroceryLabelNorwegianTests
{
    [Theory]
    [InlineData("ost", "oster")]
    [InlineData("egg", "egger")]
    [InlineData("bananer", "banan")]
    [InlineData("tomat", "tomater")]
    [InlineData("poteter", "potet")]
    [InlineData("bleie", "bleier")]
    [InlineData("Melk ", "melk")]
    [InlineData("BRØD", "brød")]
    [InlineData("🥛 melk", "melk")]
    [InlineData("melk!!", "melk")]
    [InlineData("pølse", "pølser")]
    [InlineData("eple", "epler")]
    public void These_spellings_meet(string a, string b) => Assert.Equal(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    [Theory]
    [InlineData("ål", "al")]
    [InlineData("høne", "hone")]
    [InlineData("kaffe", "filterkaffe")]
    [InlineData("toalettpapir", "tørkerull")]
    [InlineData("melk", "melon")]
    [InlineData("egg", "eggerøre")]
    public void These_stay_apart(string a, string b) => Assert.NotEqual(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    /// <summary>Known wrong splits (documented, not endorsed): the test pins today's behaviour so a change is noticed.</summary>
    [Theory]
    [InlineData("ost", "osten")] // definite form
    [InlineData("melk", "2 liter melk")] // number and unit change the key
    [InlineData("melk", "melk 2l")]
    [InlineData("gulrot", "gulrøtter")] // irregular plural
    [InlineData("brød", "brødet")]
    public void Known_wrong_splits(string a, string b) => Assert.NotEqual(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    /// <summary>Known wrong merges (documented): short words that happen to end in -e / -er meet a different word.</summary>
    [Theory]
    [InlineData("lime", "lim")] // fruit and glue
    [InlineData("vine", "vin")]
    [InlineData("oste", "ost")]
    public void Known_wrong_merges(string a, string b) => Assert.Equal(GroceryLabel.Normalize(a), GroceryLabel.Normalize(b));

    [Theory]
    [InlineData("🥛")]
    [InlineData("!!!")]
    [InlineData("   ")]
    public void Emoji_only_and_punctuation_only_labels_have_an_empty_key(string label) => Assert.Equal(string.Empty, GroceryLabel.Normalize(label));
}
