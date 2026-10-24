namespace Foodplanner.Domain.Catalog;

/// <summary>
/// THE data file for the global ingredient catalog (T-020): about a hundred common Norwegian-kitchen ingredients.
/// Names are Norwegian bokmål (Q-013 is open: to change or translate them, edit only the Name column here).
/// Key is the stable, ASCII identity of the row and gives its fixed id (<see cref="SeedIds"/>); never change a key.
/// A changed name or flag reaches existing databases only through a new migration (the seed is migration data).
/// Dimension is what the ingredient is normally measured in. Staples (never auto-added to a shopping list) are
/// the things nearly every kitchen has: salt, pepper, sugar, flour, oil, butter, baking powder and the like.
/// </summary>
public static class IngredientSeed
{
    public sealed record Row(string Key, string Name, UnitDimension Dimension, bool IsStaple = false)
    {
        public Guid Id => SeedIds.For("ingredient", Key);
    }

    private const UnitDimension M = UnitDimension.Mass;
    private const UnitDimension V = UnitDimension.Volume;
    private const UnitDimension C = UnitDimension.Count;

    public static IReadOnlyList<Row> All { get; } =
    [
        // Grønnsaker
        new("loek", "Løk", C),
        new("rodloek", "Rødløk", C),
        new("hvitloek", "Hvitløk", C),
        new("vaarloek", "Vårløk", C),
        new("purre", "Purre", C),
        new("gulrot", "Gulrot", C),
        new("potet", "Potet", M),
        new("soetpotet", "Søtpotet", M),
        new("brokkoli", "Brokkoli", M),
        new("blomkaal", "Blomkål", M),
        new("hvitkaal", "Hvitkål", M),
        new("tomat", "Tomat", C),
        new("hermetiske_tomater", "Hermetiske tomater", M),
        new("tomatpure", "Tomatpuré", M),
        new("agurk", "Agurk", C),
        new("paprika", "Paprika", C),
        new("squash", "Squash", C),
        new("aubergine", "Aubergine", C),
        new("mais", "Mais", M),
        new("erter", "Erter", M),
        new("spinat", "Spinat", M),
        new("salat", "Salat", C),
        new("sopp", "Sopp", M),
        new("ingefaer", "Ingefær", M),
        new("chili", "Chili", C),
        new("selleri", "Selleri", C),

        // Frukt
        new("eple", "Eple", C),
        new("banan", "Banan", C),
        new("sitron", "Sitron", C),
        new("lime", "Lime", C),
        new("appelsin", "Appelsin", C),
        new("jordbaer", "Jordbær", M),
        new("blaabaer", "Blåbær", M),

        // Kjøtt og fisk
        new("kyllingfilet", "Kyllingfilet", M),
        new("kyllinglaar", "Kyllinglår", M),
        new("kjottdeig", "Kjøttdeig", M),
        new("svinekotelett", "Svinekotelett", C),
        new("bacon", "Bacon", M),
        new("skinke", "Skinke", M),
        new("polser", "Pølser", C),
        new("laks", "Laks", M),
        new("torsk", "Torsk", M),
        new("reker", "Reker", M),
        new("tunfisk", "Tunfisk", M),
        new("fiskepinner", "Fiskepinner", C),

        // Meieri og egg
        new("melk", "Melk", V),
        new("floete", "Fløte", V),
        new("matfloete", "Matfløte", V),
        new("romme", "Rømme", V),
        new("creme_fraiche", "Crème fraîche", V),
        new("yoghurt", "Yoghurt naturell", V),
        new("smor", "Smør", M, IsStaple: true),
        new("ost", "Ost", M),
        new("mozzarella", "Mozzarella", M),
        new("parmesan", "Parmesan", M),
        new("brunost", "Brunost", M),
        new("egg", "Egg", C),
        new("cottage_cheese", "Cottage cheese", M),

        // Tørrvarer og hermetikk
        new("pasta", "Pasta", M),
        new("spaghetti", "Spaghetti", M),
        new("ris", "Ris", M),
        new("couscous", "Couscous", M),
        new("nudler", "Nudler", M),
        new("havregryn", "Havregryn", M),
        new("brod", "Brød", C),
        new("tortillalefser", "Tortillalefser", C),
        new("linser", "Linser", M),
        new("kikerter", "Kikerter", M),
        new("kidneybonner", "Kidneybønner", M),
        new("kokosmelk", "Kokosmelk", V),
        new("buljongterning", "Buljongterning", C),
        new("soyasaus", "Soyasaus", V),
        new("sennep", "Sennep", M),
        new("ketchup", "Ketchup", M),
        new("majones", "Majones", M),
        new("honning", "Honning", M),
        new("sirup", "Sirup", V),
        new("peanottsmor", "Peanøttsmør", M),

        // Krydder
        new("salt", "Salt", M, IsStaple: true),
        new("sort_pepper", "Sort pepper", M, IsStaple: true),
        new("paprikapulver", "Paprikapulver", M),
        new("kanel", "Kanel", M),
        new("spisskummen", "Spisskummen", M),
        new("oregano", "Oregano", M),
        new("timian", "Timian", M),
        new("basilikum", "Basilikum", M),
        new("persille", "Persille", M),
        new("gressloek", "Gressløk", M),
        new("karri", "Karri", M),
        new("muskat", "Muskat", M),
        new("laurbaerblad", "Laurbærblad", C),
        new("kardemomme", "Kardemomme", M),

        // Baking
        new("hvetemel", "Hvetemel", M, IsStaple: true),
        new("sukker", "Sukker", M, IsStaple: true),
        new("melis", "Melis", M),
        new("brunt_sukker", "Brunt sukker", M),
        new("bakepulver", "Bakepulver", M, IsStaple: true),
        new("natron", "Natron", M, IsStaple: true),
        new("vaniljesukker", "Vaniljesukker", M),
        new("kakao", "Kakao", M),
        new("gjaer", "Gjær", M),
        new("maisenna", "Maisenna", M),
        new("sjokolade", "Sjokolade", M),

        // Olje, eddik og vann
        new("rapsolje", "Rapsolje", V, IsStaple: true),
        new("olivenolje", "Olivenolje", V, IsStaple: true),
        new("eddik", "Eddik", V, IsStaple: true),
        new("vann", "Vann", V, IsStaple: true),
    ];
}
