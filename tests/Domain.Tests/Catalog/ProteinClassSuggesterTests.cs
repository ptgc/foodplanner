using Foodplanner.Domain.Catalog;

namespace Foodplanner.Domain.Tests.Catalog;

public class ProteinClassSuggesterTests
{
    // The own ingredients that earlier matprat.no imports left as Unknown (T-104), plus the plant staples a recipe holds.
    [Theory]
    [InlineData("Grillet kylling", ProteinClass.Chicken)]
    [InlineData("Torskefilet eller annen kokt", ProteinClass.Fish)]
    [InlineData("Karbonadedeig", ProteinClass.Meat)]
    [InlineData("Kjøttpølse", ProteinClass.Meat)]
    [InlineData("Pepperoni", ProteinClass.Meat)]
    [InlineData("Kremfløte", ProteinClass.AnimalProduct)]
    [InlineData("Revet hvitost", ProteinClass.AnimalProduct)]
    [InlineData("løk", ProteinClass.None)]
    [InlineData("hvitløk", ProteinClass.None)]
    [InlineData("rødløk", ProteinClass.None)]
    [InlineData("tomat", ProteinClass.None)]
    [InlineData("hermetiske tomater", ProteinClass.None)]
    [InlineData("paprika", ProteinClass.None)]
    [InlineData("hvetemel", ProteinClass.None)]
    [InlineData("mel", ProteinClass.None)]
    [InlineData("sukker", ProteinClass.None)]
    [InlineData("salt", ProteinClass.None)]
    [InlineData("pepper", ProteinClass.None)]
    [InlineData("pasta", ProteinClass.None)]
    [InlineData("spagetti", ProteinClass.None)]
    [InlineData("basmatiris", ProteinClass.None)]
    [InlineData("ris", ProteinClass.None)]
    [InlineData("olivenolje", ProteinClass.None)]
    [InlineData("olje", ProteinClass.None)]
    [InlineData("gulrot", ProteinClass.None)]
    [InlineData("persille", ProteinClass.None)]
    public void The_names_from_earlier_imports_get_their_class(string name, ProteinClass expected) =>
        Assert.Equal(expected, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("kyllingfilet", ProteinClass.Chicken)]
    [InlineData("kalkun", ProteinClass.Chicken)]
    [InlineData("laks", ProteinClass.Fish)]
    [InlineData("Røkt laks", ProteinClass.Fish)]
    [InlineData("sei", ProteinClass.Fish)]
    [InlineData("hysefilet", ProteinClass.Fish)]
    [InlineData("makrell i tomat", ProteinClass.Fish)]
    [InlineData("tunfisk", ProteinClass.Fish)]
    [InlineData("hvit fisk", ProteinClass.Fish)]
    [InlineData("reker", ProteinClass.Seafood)]
    [InlineData("scampi", ProteinClass.Seafood)]
    [InlineData("krabbe", ProteinClass.Seafood)]
    [InlineData("blåskjell", ProteinClass.Seafood)]
    [InlineData("svinekjøtt", ProteinClass.Pork)]
    [InlineData("bacon", ProteinClass.Pork)]
    [InlineData("kokt skinke", ProteinClass.Pork)]
    [InlineData("storfekjøtt", ProteinClass.Beef)]
    [InlineData("oksefilet", ProteinClass.Beef)]
    [InlineData("biff", ProteinClass.Beef)]
    [InlineData("kjøttdeig av storfe", ProteinClass.Beef)]
    [InlineData("kjøttdeig", ProteinClass.Meat)]
    [InlineData("kjøttdeig av svin og storfe", ProteinClass.Meat)]
    [InlineData("salami", ProteinClass.Meat)]
    [InlineData("lam", ProteinClass.Lamb)]
    [InlineData("lammekotelett", ProteinClass.Lamb)]
    [InlineData("fårikål", ProteinClass.Lamb)]
    [InlineData("ost", ProteinClass.AnimalProduct)]
    [InlineData("Parmesan", ProteinClass.AnimalProduct)]
    [InlineData("fløte", ProteinClass.AnimalProduct)]
    [InlineData("melk", ProteinClass.AnimalProduct)]
    [InlineData("lettmelk", ProteinClass.AnimalProduct)]
    [InlineData("rømme", ProteinClass.AnimalProduct)]
    [InlineData("smør", ProteinClass.AnimalProduct)]
    [InlineData("hvitløksmør", ProteinClass.AnimalProduct)]
    [InlineData("egg", ProteinClass.AnimalProduct)]
    [InlineData("eggeplomme", ProteinClass.AnimalProduct)]
    [InlineData("crème fraîche", ProteinClass.AnimalProduct)]
    [InlineData("chicken breast", ProteinClass.Chicken)]
    [InlineData("salmon fillet", ProteinClass.Fish)]
    [InlineData("shrimp", ProteinClass.Seafood)]
    [InlineData("ground beef", ProteinClass.Beef)]
    [InlineData("minced meat", ProteinClass.Meat)]
    [InlineData("grated cheese", ProteinClass.AnimalProduct)]
    [InlineData("heavy cream", ProteinClass.AnimalProduct)]
    [InlineData("butter", ProteinClass.AnimalProduct)]
    [InlineData("pork sausages", ProteinClass.Pork)]
    public void Keywords_and_compounds_are_recognised(string name, ProteinClass expected) =>
        Assert.Equal(expected, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("peanøttsmør", ProteinClass.None)]
    [InlineData("mandelsmør", ProteinClass.None)]
    [InlineData("kokosmelk", ProteinClass.None)]
    [InlineData("havremelk", ProteinClass.None)]
    [InlineData("soyamelk", ProteinClass.None)]
    [InlineData("kokosfløte", ProteinClass.None)]
    [InlineData("soyayoghurt", ProteinClass.None)]
    [InlineData("coconut milk", ProteinClass.None)]
    [InlineData("peanut butter", ProteinClass.None)]
    [InlineData("oat milk", ProteinClass.None)]
    [InlineData("eggplante", ProteinClass.None)]
    [InlineData("eggplant", ProteinClass.None)]
    [InlineData("butternut squash", ProteinClass.None)]
    [InlineData("vegetarpølse", ProteinClass.None)]
    [InlineData("vegansk ost", ProteinClass.None)]
    [InlineData("kost", ProteinClass.Unknown)]
    [InlineData("fiskesaus", ProteinClass.Fish)]
    [InlineData("eggnudler", ProteinClass.AnimalProduct)]
    [InlineData("kyllingbuljong", ProteinClass.Unknown)]
    [InlineData("grønnsakskraft", ProteinClass.Unknown)]
    [InlineData("pizzadeig", ProteinClass.Unknown)]
    [InlineData("deig", ProteinClass.Unknown)]
    [InlineData("fersk basilikum", ProteinClass.None)]
    public void Documented_decisions_for_ambiguous_names(string name, ProteinClass expected) =>
        Assert.Equal(expected, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("pølsebrød", ProteinClass.None)]
    [InlineData("hamburgerbrød", ProteinClass.None)]
    [InlineData("lomper", ProteinClass.None)]
    [InlineData("pizzabunn", ProteinClass.None)]
    [InlineData("kjeks", ProteinClass.None)]
    [InlineData("krabbekjøtt", ProteinClass.Seafood)]
    [InlineData("hummerkjøtt", ProteinClass.Seafood)]
    [InlineData("fiskekjøtt", ProteinClass.Fish)]
    [InlineData("kyllingkjøtt", ProteinClass.Chicken)]
    [InlineData("svinekjøtt", ProteinClass.Pork)]
    [InlineData("lammekjøtt", ProteinClass.Lamb)]
    [InlineData("kjøttboller", ProteinClass.Meat)]
    [InlineData("svinekjøtt og storfekjøtt", ProteinClass.Meat)]
    [InlineData("andebryst", ProteinClass.Meat)]
    [InlineData("andelår", ProteinClass.Meat)]
    [InlineData("gås", ProteinClass.Meat)]
    [InlineData("hjort", ProteinClass.Meat)]
    [InlineData("hjortekjøtt", ProteinClass.Meat)]
    [InlineData("elg", ProteinClass.Meat)]
    [InlineData("elgkjøtt", ProteinClass.Meat)]
    [InlineData("reinsdyr", ProteinClass.Meat)]
    [InlineData("reinskav", ProteinClass.Meat)]
    [InlineData("viltkjøtt", ProteinClass.Meat)]
    [InlineData("vilt", ProteinClass.Meat)]
    [InlineData("duck breast", ProteinClass.Meat)]
    [InlineData("hamburger", ProteinClass.Meat)]
    [InlineData("hamburgerrygg", ProteinClass.Pork)]
    [InlineData("tubekaviar", ProteinClass.Fish)]
    [InlineData("kaviar", ProteinClass.Fish)]
    [InlineData("hummus", ProteinClass.None)]
    [InlineData("tahini", ProteinClass.None)]
    [InlineData("steinsopp", ProteinClass.None)]
    [InlineData("gnocchi", ProteinClass.None)]
    [InlineData("lever", ProteinClass.Meat)]
    [InlineData("kyllinglever", ProteinClass.Chicken)]
    // "and" alone is the English conjunction and "andre"/"andel" are other words: none of them is duck.
    [InlineData("salt and pepper", ProteinClass.None)]
    [InlineData("andre tomater", ProteinClass.None)]
    [InlineData("andel", ProteinClass.Unknown)]
    public void Fix_round_bread_specific_over_generic_game_and_gaps(string name, ProteinClass expected) =>
        Assert.Equal(expected, ProteinClassSuggester.Suggest(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyzzy")]
    [InlineData("noe rart")]
    public void An_unknown_name_stays_unknown(string name) => Assert.Equal(ProteinClass.Unknown, ProteinClassSuggester.Suggest(name));

    [Fact]
    public void Null_is_unknown() => Assert.Equal(ProteinClass.Unknown, ProteinClassSuggester.Suggest(null));

    [Fact]
    public void An_exact_catalog_name_beats_the_keywords()
    {
        var catalog = new Dictionary<string, ProteinClass>
        {
            [ProteinClassSuggester.CatalogKey("Fiskesaus")] = ProteinClass.None, // a household decided fish sauce is fine
            [ProteinClassSuggester.CatalogKey("Rømmegrøt")] = ProteinClass.AnimalProduct,
            [ProteinClassSuggester.CatalogKey("Mystery")] = ProteinClass.Unknown,
        };

        Assert.Equal(ProteinClass.None, ProteinClassSuggester.Suggest("fiskesaus", catalog));
        Assert.Equal(ProteinClass.AnimalProduct, ProteinClassSuggester.Suggest("RØMMEGRØT", catalog));
        Assert.Equal(ProteinClass.Fish, ProteinClassSuggester.Suggest("laks", catalog)); // not in the catalog: keywords
        Assert.Equal(ProteinClass.Unknown, ProteinClassSuggester.Suggest("Mystery", catalog)); // Unknown entries are ignored
    }
}
