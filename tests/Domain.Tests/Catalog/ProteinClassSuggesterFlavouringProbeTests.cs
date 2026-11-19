using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Tests.Catalog;

/// <summary>Verifier probes for T-108: the per-word flavouring guard must not hide real protein and must not misfire on look-alike words.</summary>
public class ProteinClassSuggesterFlavouringProbeTests
{
    [Theory]
    [InlineData("kyllingfilet i buljong", ProteinClass.Chicken)]
    [InlineData("kyllingkraft med kylling", ProteinClass.Chicken)]
    [InlineData("fiskekraft med torsk", ProteinClass.Fish)]
    [InlineData("kjøttkraft og storfekjøtt", ProteinClass.Beef)]
    [InlineData("kjøttkrydder og kjøttdeig", ProteinClass.Meat)]
    [InlineData("kyllingkrydder og kyllingfilet", ProteinClass.Chicken)]
    [InlineData("kyllingvinger med grillkrydder", ProteinClass.Chicken)]
    [InlineData("bacon i kraft", ProteinClass.Pork)]
    [InlineData("kjøttsaus", ProteinClass.Meat)]
    [InlineData("kjøttboller i saus", ProteinClass.Meat)]
    [InlineData("kyllingsaus", ProteinClass.Chicken)]
    [InlineData("fiskekaker", ProteinClass.Fish)]
    [InlineData("fiskesuppe", ProteinClass.Fish)]
    [InlineData("kyllingsuppe", ProteinClass.Chicken)]
    [InlineData("fiskesaus og laks", ProteinClass.Fish)]
    [InlineData("rekepasta og reker", ProteinClass.Seafood)]
    [InlineData("østerssaus med biff", ProteinClass.Beef)]
    [InlineData("eggnudler med kylling", ProteinClass.Chicken)]
    [InlineData("eggnudler med reker", ProteinClass.Seafood)]
    [InlineData("egg noodles with chicken", ProteinClass.Chicken)]
    [InlineData("laks i soyasaus", ProteinClass.Fish)]
    [InlineData("soyabønner med kylling", ProteinClass.Chicken)]
    [InlineData("egg", ProteinClass.AnimalProduct)]
    [InlineData("eggerøre", ProteinClass.AnimalProduct)]
    [InlineData("tofu med egg", ProteinClass.AnimalProduct)]
    [InlineData("kraftost", ProteinClass.AnimalProduct)]
    [InlineData("krydderost", ProteinClass.AnimalProduct)]
    [InlineData("krydderskinke", ProteinClass.Pork)]
    [InlineData("lakserogn", ProteinClass.Fish)]
    [InlineData("rogn", ProteinClass.Fish)]
    [InlineData("kryddersmør", ProteinClass.AnimalProduct)]
    [InlineData("ostesaus", ProteinClass.AnimalProduct)]
    public void Real_protein_survives_next_to_flavouring_words(string name, ProteinClass expected) =>
        Assert.Equal(expected, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("oksebuljong")]
    [InlineData("oksekraft")]
    [InlineData("kalvefond")]
    [InlineData("kalkunfond")]
    [InlineData("hønsekraft")]
    [InlineData("fiskebuljong")]
    [InlineData("kjøttbuljong")]
    [InlineData("krabbekraft")]
    [InlineData("hummerfond")]
    [InlineData("beef stock")]
    [InlineData("fish stock cube")]
    [InlineData("chicken bouillon cube")]
    [InlineData("pizzakrydder")]
    [InlineData("svinekrydder")]
    [InlineData("laksekrydder")]
    [InlineData("baconkrydder")]
    [InlineData("beef seasoning")]
    [InlineData("prawn paste")]
    [InlineData("oyster sauce")]
    [InlineData("tørkede eggnudler")]
    [InlineData("eggenudler")]
    [InlineData("eggnudel")]
    [InlineData("soyamelk")]
    [InlineData("soyabønner")]
    [InlineData("tofu")]
    [InlineData("rognebærgele")]
    public void More_flavourings_and_plant_look_alikes_are_none(string name) =>
        Assert.Equal(ProteinClass.None, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("fondue")]
    [InlineData("fondant")]
    [InlineData("kraftig")]
    [InlineData("worcestershiresaus")]
    [InlineData("hoisinsaus")]
    [InlineData("sjømatsaus")]
    public void Look_alike_words_are_not_swallowed_as_flavourings(string name) =>
        Assert.Equal(ProteinClass.Unknown, ProteinClassSuggester.Suggest(name));
}
