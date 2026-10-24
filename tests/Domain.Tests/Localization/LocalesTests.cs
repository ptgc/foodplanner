using Foodplanner.Domain.Localization;

namespace Foodplanner.Domain.Tests.Localization;

public class LocalesTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("nb", "nb")]
    [InlineData("nb-NO", "nb")]
    [InlineData("no", "nb")]
    [InlineData("nn-NO", "nb")]
    [InlineData("NB-no", "nb")]
    [InlineData("fr", "en")]
    [InlineData("*", "en")]
    [InlineData("nb,en;q=0.8", "nb")]
    [InlineData("en;q=0.8,nb;q=0.9", "nb")]
    [InlineData("en-US,en;q=0.9,nb;q=0.5", "en")]
    [InlineData("fr-FR,fr;q=0.9,nb;q=0.5", "nb")]
    [InlineData("nb;q=0,en;q=0.1", "en")]
    [InlineData("nb;q=0", "en")]
    [InlineData("de, nb;q=0.7, en;q=0.7", "nb")]
    [InlineData("nb;q=abc,en;q=0.5", "en")]
    [InlineData("nb;q=2,en;q=0.5", "en")]
    [InlineData(";;,,;q=1", "en")]
    [InlineData("nb ; q=0.9 , en ; q=0.8", "nb")]
    public void Negotiate_picks_the_best_supported_locale(string? header, string expected) =>
        Assert.Equal(expected, Locales.Negotiate(header));

    [Fact]
    public void Default_is_english_and_both_locales_are_supported()
    {
        Assert.Equal("en", Locales.Default);
        Assert.Equal(["nb", "en"], Locales.Supported);
    }
}
