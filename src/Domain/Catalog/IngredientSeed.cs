using Foodplanner.Domain.Localization;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Catalog;

/// <summary>
/// THE data file for the global ingredient catalog (T-020): about a hundred common Norwegian-kitchen ingredients.
/// Name is the English name (also the last-resort fallback and the name stored on the row itself); Nb is the Norwegian bokmål name (Q-013, T-039).
/// More locales: add a column here and to <c>Names</c>; the schema does not change.
/// Key is the stable, ASCII identity of the row and gives its fixed id (<see cref="SeedIds"/>); never change a key.
/// A changed name or flag reaches existing databases only through a new migration (the seed is migration data).
/// Dimension is what the ingredient is normally measured in. Section is the store section it is found in (T-085): every row must have one, it is a required constructor argument. Staples (never auto-added to a shopping list) are
/// the things nearly every kitchen has: salt, pepper, sugar, flour, oil, butter, baking powder and the like.
/// </summary>
public static class IngredientSeed
{
    public sealed record Row(string Key, string Name, string Nb, UnitDimension Dimension, StoreSection Section, bool IsStaple = false)
    {
        /// <summary>The name in every locale, by locale code.</summary>
        public IReadOnlyDictionary<string, string> Names => new Dictionary<string, string> { [Locales.English] = Name, [Locales.Norwegian] = Nb };

        public Guid Id => SeedIds.For("ingredient", Key);
    }

    private const UnitDimension M = UnitDimension.Mass;
    private const UnitDimension V = UnitDimension.Volume;
    private const UnitDimension C = UnitDimension.Count;

    private const StoreSection Produce = StoreSection.FruitAndVegetables;
    private const StoreSection Meat = StoreSection.MeatFishAndCheese;
    private const StoreSection Frozen = StoreSection.Frozen;
    private const StoreSection Dairy = StoreSection.Dairy;
    private const StoreSection Dry = StoreSection.DryGoods;
    private const StoreSection Other = StoreSection.Other;

    public static IReadOnlyList<Row> All { get; } =
    [
        // Grønnsaker
        new("loek", "Onion", "Løk", C, Produce),
        new("rodloek", "Red onion", "Rødløk", C, Produce),
        new("hvitloek", "Garlic", "Hvitløk", C, Produce),
        new("vaarloek", "Spring onion", "Vårløk", C, Produce),
        new("purre", "Leek", "Purre", C, Produce),
        new("gulrot", "Carrot", "Gulrot", C, Produce),
        new("potet", "Potato", "Potet", M, Produce),
        new("soetpotet", "Sweet potato", "Søtpotet", M, Produce),
        new("brokkoli", "Broccoli", "Brokkoli", M, Produce),
        new("blomkaal", "Cauliflower", "Blomkål", M, Produce),
        new("hvitkaal", "White cabbage", "Hvitkål", M, Produce),
        new("tomat", "Tomato", "Tomat", C, Produce),
        new("hermetiske_tomater", "Canned tomatoes", "Hermetiske tomater", M, Dry),
        new("tomatpure", "Tomato puree", "Tomatpuré", M, Dry),
        new("agurk", "Cucumber", "Agurk", C, Produce),
        new("paprika", "Bell pepper", "Paprika", C, Produce),
        new("squash", "Zucchini", "Squash", C, Produce),
        new("aubergine", "Eggplant", "Aubergine", C, Produce),
        new("mais", "Corn", "Mais", M, Dry),
        new("erter", "Peas", "Erter", M, Frozen),
        new("spinat", "Spinach", "Spinat", M, Produce),
        new("salat", "Lettuce", "Salat", C, Produce),
        new("sopp", "Mushrooms", "Sopp", M, Produce),
        new("ingefaer", "Ginger", "Ingefær", M, Produce),
        new("chili", "Chili", "Chili", C, Produce),
        new("selleri", "Celery", "Selleri", C, Produce),

        // Frukt
        new("eple", "Apple", "Eple", C, Produce),
        new("banan", "Banana", "Banan", C, Produce),
        new("sitron", "Lemon", "Sitron", C, Produce),
        new("lime", "Lime", "Lime", C, Produce),
        new("appelsin", "Orange", "Appelsin", C, Produce),
        new("jordbaer", "Strawberries", "Jordbær", M, Produce),
        new("blaabaer", "Blueberries", "Blåbær", M, Produce),

        // Kjøtt og fisk
        new("kyllingfilet", "Chicken breast fillet", "Kyllingfilet", M, Meat),
        new("kyllinglaar", "Chicken thighs", "Kyllinglår", M, Meat),
        new("kjottdeig", "Minced meat", "Kjøttdeig", M, Meat),
        new("svinekotelett", "Pork chop", "Svinekotelett", C, Meat),
        new("bacon", "Bacon", "Bacon", M, Meat),
        new("skinke", "Ham", "Skinke", M, Meat),
        new("polser", "Sausages", "Pølser", C, Meat),
        new("laks", "Salmon", "Laks", M, Meat),
        new("torsk", "Cod", "Torsk", M, Meat),
        new("reker", "Shrimp", "Reker", M, Meat),
        new("tunfisk", "Tuna", "Tunfisk", M, Dry),
        new("fiskepinner", "Fish fingers", "Fiskepinner", C, Frozen),

        // Meieri og egg
        new("melk", "Milk", "Melk", V, Dairy),
        new("floete", "Cream", "Fløte", V, Dairy),
        new("matfloete", "Cooking cream", "Matfløte", V, Dairy),
        new("romme", "Sour cream", "Rømme", V, Dairy),
        new("creme_fraiche", "Crème fraîche", "Crème fraîche", V, Dairy),
        new("yoghurt", "Plain yoghurt", "Yoghurt naturell", V, Dairy),
        new("smor", "Butter", "Smør", M, Dairy, IsStaple: true),
        new("ost", "Cheese", "Ost", M, Meat),
        new("mozzarella", "Mozzarella", "Mozzarella", M, Meat),
        new("parmesan", "Parmesan", "Parmesan", M, Meat),
        new("brunost", "Brown cheese", "Brunost", M, Meat),
        new("egg", "Egg", "Egg", C, Dairy),
        new("cottage_cheese", "Cottage cheese", "Cottage cheese", M, Dairy),

        // Tørrvarer og hermetikk
        new("pasta", "Pasta", "Pasta", M, Dry),
        new("spaghetti", "Spaghetti", "Spaghetti", M, Dry),
        new("ris", "Rice", "Ris", M, Dry),
        new("couscous", "Couscous", "Couscous", M, Dry),
        new("nudler", "Noodles", "Nudler", M, Dry),
        new("havregryn", "Rolled oats", "Havregryn", M, Dry),
        new("brod", "Bread", "Brød", C, Dry),
        new("tortillalefser", "Tortilla wraps", "Tortillalefser", C, Dry),
        new("linser", "Lentils", "Linser", M, Dry),
        new("kikerter", "Chickpeas", "Kikerter", M, Dry),
        new("kidneybonner", "Kidney beans", "Kidneybønner", M, Dry),
        new("kokosmelk", "Coconut milk", "Kokosmelk", V, Dry),
        new("buljongterning", "Stock cube", "Buljongterning", C, Dry),
        new("soyasaus", "Soy sauce", "Soyasaus", V, Dry),
        new("sennep", "Mustard", "Sennep", M, Dry),
        new("ketchup", "Ketchup", "Ketchup", M, Dry),
        new("majones", "Mayonnaise", "Majones", M, Dry),
        new("honning", "Honey", "Honning", M, Dry),
        new("sirup", "Syrup", "Sirup", V, Dry),
        new("peanottsmor", "Peanut butter", "Peanøttsmør", M, Dry),

        // Krydder
        new("salt", "Salt", "Salt", M, Dry, IsStaple: true),
        new("sort_pepper", "Black pepper", "Sort pepper", M, Dry, IsStaple: true),
        new("paprikapulver", "Paprika powder", "Paprikapulver", M, Dry),
        new("kanel", "Cinnamon", "Kanel", M, Dry),
        new("spisskummen", "Cumin", "Spisskummen", M, Dry),
        new("oregano", "Oregano", "Oregano", M, Dry),
        new("timian", "Thyme", "Timian", M, Produce),
        new("basilikum", "Basil", "Basilikum", M, Produce),
        new("persille", "Parsley", "Persille", M, Produce),
        new("gressloek", "Chives", "Gressløk", M, Produce),
        new("karri", "Curry powder", "Karri", M, Dry),
        new("muskat", "Nutmeg", "Muskat", M, Dry),
        new("laurbaerblad", "Bay leaf", "Laurbærblad", C, Dry),
        new("kardemomme", "Cardamom", "Kardemomme", M, Dry),

        // Baking
        new("hvetemel", "Wheat flour", "Hvetemel", M, Dry, IsStaple: true),
        new("sukker", "Sugar", "Sukker", M, Dry, IsStaple: true),
        new("melis", "Icing sugar", "Melis", M, Dry),
        new("brunt_sukker", "Brown sugar", "Brunt sukker", M, Dry),
        new("bakepulver", "Baking powder", "Bakepulver", M, Dry, IsStaple: true),
        new("natron", "Baking soda", "Natron", M, Dry, IsStaple: true),
        new("vaniljesukker", "Vanilla sugar", "Vaniljesukker", M, Dry),
        new("kakao", "Cocoa", "Kakao", M, Dry),
        new("gjaer", "Yeast", "Gjær", M, Dry),
        new("maisenna", "Cornstarch", "Maisenna", M, Dry),
        new("sjokolade", "Chocolate", "Sjokolade", M, Dry),

        // Olje, eddik og vann
        new("rapsolje", "Rapeseed oil", "Rapsolje", V, Dry, IsStaple: true),
        new("olje", "Oil", "Olje", V, Dry, IsStaple: true),
        new("noytral_olje", "Neutral oil", "Nøytral olje", V, Dry, IsStaple: true),
        new("margarin", "Margarine", "Margarin", M, Dairy, IsStaple: true),
        new("olivenolje", "Olive oil", "Olivenolje", V, Dry, IsStaple: true),
        new("eddik", "Vinegar", "Eddik", V, Dry, IsStaple: true),
        new("vann", "Water", "Vann", V, Other, IsStaple: true),
    ];
}
