using Foodplanner.Domain.Localization;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// THE data file for the global ingredient catalog (T-020): about a hundred common Norwegian-kitchen ingredients.
/// Name is the English name (also the last-resort fallback and the name stored on the row itself); Nb is the Norwegian bokmål name (Q-013, T-039).
/// More locales: add a column here and to <c>Names</c>; the schema does not change.
/// Key is the stable, ASCII identity of the row and gives its fixed id (<see cref="SeedIds"/>); never change a key.
/// A changed name or flag reaches existing databases only through a new migration (the seed is migration data).
/// Dimension is what the ingredient is normally measured in. Staples (never auto-added to a shopping list) are
/// the things nearly every kitchen has: salt, pepper, sugar, flour, oil, butter, baking powder and the like.
/// </summary>
public static class IngredientSeed
{
    public sealed record Row(string Key, string Name, string Nb, UnitDimension Dimension, bool IsStaple = false)
    {
        /// <summary>The name in every locale, by locale code.</summary>
        public IReadOnlyDictionary<string, string> Names => new Dictionary<string, string> { [Locales.English] = Name, [Locales.Norwegian] = Nb };

        public Guid Id => SeedIds.For("ingredient", Key);
    }

    private const UnitDimension M = UnitDimension.Mass;
    private const UnitDimension V = UnitDimension.Volume;
    private const UnitDimension C = UnitDimension.Count;

    public static IReadOnlyList<Row> All { get; } =
    [
        // Grønnsaker
        new("loek", "Onion", "Løk", C),
        new("rodloek", "Red onion", "Rødløk", C),
        new("hvitloek", "Garlic", "Hvitløk", C),
        new("vaarloek", "Spring onion", "Vårløk", C),
        new("purre", "Leek", "Purre", C),
        new("gulrot", "Carrot", "Gulrot", C),
        new("potet", "Potato", "Potet", M),
        new("soetpotet", "Sweet potato", "Søtpotet", M),
        new("brokkoli", "Broccoli", "Brokkoli", M),
        new("blomkaal", "Cauliflower", "Blomkål", M),
        new("hvitkaal", "White cabbage", "Hvitkål", M),
        new("tomat", "Tomato", "Tomat", C),
        new("hermetiske_tomater", "Canned tomatoes", "Hermetiske tomater", M),
        new("tomatpure", "Tomato puree", "Tomatpuré", M),
        new("agurk", "Cucumber", "Agurk", C),
        new("paprika", "Bell pepper", "Paprika", C),
        new("squash", "Zucchini", "Squash", C),
        new("aubergine", "Eggplant", "Aubergine", C),
        new("mais", "Corn", "Mais", M),
        new("erter", "Peas", "Erter", M),
        new("spinat", "Spinach", "Spinat", M),
        new("salat", "Lettuce", "Salat", C),
        new("sopp", "Mushrooms", "Sopp", M),
        new("ingefaer", "Ginger", "Ingefær", M),
        new("chili", "Chili", "Chili", C),
        new("selleri", "Celery", "Selleri", C),

        // Frukt
        new("eple", "Apple", "Eple", C),
        new("banan", "Banana", "Banan", C),
        new("sitron", "Lemon", "Sitron", C),
        new("lime", "Lime", "Lime", C),
        new("appelsin", "Orange", "Appelsin", C),
        new("jordbaer", "Strawberries", "Jordbær", M),
        new("blaabaer", "Blueberries", "Blåbær", M),

        // Kjøtt og fisk
        new("kyllingfilet", "Chicken breast fillet", "Kyllingfilet", M),
        new("kyllinglaar", "Chicken thighs", "Kyllinglår", M),
        new("kjottdeig", "Minced meat", "Kjøttdeig", M),
        new("svinekotelett", "Pork chop", "Svinekotelett", C),
        new("bacon", "Bacon", "Bacon", M),
        new("skinke", "Ham", "Skinke", M),
        new("polser", "Sausages", "Pølser", C),
        new("laks", "Salmon", "Laks", M),
        new("torsk", "Cod", "Torsk", M),
        new("reker", "Shrimp", "Reker", M),
        new("tunfisk", "Tuna", "Tunfisk", M),
        new("fiskepinner", "Fish fingers", "Fiskepinner", C),

        // Meieri og egg
        new("melk", "Milk", "Melk", V),
        new("floete", "Cream", "Fløte", V),
        new("matfloete", "Cooking cream", "Matfløte", V),
        new("romme", "Sour cream", "Rømme", V),
        new("creme_fraiche", "Crème fraîche", "Crème fraîche", V),
        new("yoghurt", "Plain yoghurt", "Yoghurt naturell", V),
        new("smor", "Butter", "Smør", M, IsStaple: true),
        new("ost", "Cheese", "Ost", M),
        new("mozzarella", "Mozzarella", "Mozzarella", M),
        new("parmesan", "Parmesan", "Parmesan", M),
        new("brunost", "Brown cheese", "Brunost", M),
        new("egg", "Egg", "Egg", C),
        new("cottage_cheese", "Cottage cheese", "Cottage cheese", M),

        // Tørrvarer og hermetikk
        new("pasta", "Pasta", "Pasta", M),
        new("spaghetti", "Spaghetti", "Spaghetti", M),
        new("ris", "Rice", "Ris", M),
        new("couscous", "Couscous", "Couscous", M),
        new("nudler", "Noodles", "Nudler", M),
        new("havregryn", "Rolled oats", "Havregryn", M),
        new("brod", "Bread", "Brød", C),
        new("tortillalefser", "Tortilla wraps", "Tortillalefser", C),
        new("linser", "Lentils", "Linser", M),
        new("kikerter", "Chickpeas", "Kikerter", M),
        new("kidneybonner", "Kidney beans", "Kidneybønner", M),
        new("kokosmelk", "Coconut milk", "Kokosmelk", V),
        new("buljongterning", "Stock cube", "Buljongterning", C),
        new("soyasaus", "Soy sauce", "Soyasaus", V),
        new("sennep", "Mustard", "Sennep", M),
        new("ketchup", "Ketchup", "Ketchup", M),
        new("majones", "Mayonnaise", "Majones", M),
        new("honning", "Honey", "Honning", M),
        new("sirup", "Syrup", "Sirup", V),
        new("peanottsmor", "Peanut butter", "Peanøttsmør", M),

        // Krydder
        new("salt", "Salt", "Salt", M, IsStaple: true),
        new("sort_pepper", "Black pepper", "Sort pepper", M, IsStaple: true),
        new("paprikapulver", "Paprika powder", "Paprikapulver", M),
        new("kanel", "Cinnamon", "Kanel", M),
        new("spisskummen", "Cumin", "Spisskummen", M),
        new("oregano", "Oregano", "Oregano", M),
        new("timian", "Thyme", "Timian", M),
        new("basilikum", "Basil", "Basilikum", M),
        new("persille", "Parsley", "Persille", M),
        new("gressloek", "Chives", "Gressløk", M),
        new("karri", "Curry powder", "Karri", M),
        new("muskat", "Nutmeg", "Muskat", M),
        new("laurbaerblad", "Bay leaf", "Laurbærblad", C),
        new("kardemomme", "Cardamom", "Kardemomme", M),

        // Baking
        new("hvetemel", "Wheat flour", "Hvetemel", M, IsStaple: true),
        new("sukker", "Sugar", "Sukker", M, IsStaple: true),
        new("melis", "Icing sugar", "Melis", M),
        new("brunt_sukker", "Brown sugar", "Brunt sukker", M),
        new("bakepulver", "Baking powder", "Bakepulver", M, IsStaple: true),
        new("natron", "Baking soda", "Natron", M, IsStaple: true),
        new("vaniljesukker", "Vanilla sugar", "Vaniljesukker", M),
        new("kakao", "Cocoa", "Kakao", M),
        new("gjaer", "Yeast", "Gjær", M),
        new("maisenna", "Cornstarch", "Maisenna", M),
        new("sjokolade", "Chocolate", "Sjokolade", M),

        // Olje, eddik og vann
        new("rapsolje", "Rapeseed oil", "Rapsolje", V, IsStaple: true),
        new("olje", "Oil", "Olje", V, IsStaple: true),
        new("noytral_olje", "Neutral oil", "Nøytral olje", V, IsStaple: true),
        new("margarin", "Margarine", "Margarin", M, IsStaple: true),
        new("olivenolje", "Olive oil", "Olivenolje", V, IsStaple: true),
        new("eddik", "Vinegar", "Eddik", V, IsStaple: true),
        new("vann", "Water", "Vann", V, IsStaple: true),
    ];
}
