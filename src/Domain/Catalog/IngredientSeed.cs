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
    public sealed record Row(string Key, string Name, string Nb, UnitDimension Dimension, StoreSection Section, ProteinClass Protein, bool IsStaple = false)
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
        new("loek", "Onion", "Løk", C, Produce, ProteinClass.None),
        new("rodloek", "Red onion", "Rødløk", C, Produce, ProteinClass.None),
        new("hvitloek", "Garlic", "Hvitløk", C, Produce, ProteinClass.None),
        new("vaarloek", "Spring onion", "Vårløk", C, Produce, ProteinClass.None),
        new("purre", "Leek", "Purre", C, Produce, ProteinClass.None),
        new("gulrot", "Carrot", "Gulrot", C, Produce, ProteinClass.None),
        new("potet", "Potato", "Potet", M, Produce, ProteinClass.None),
        new("soetpotet", "Sweet potato", "Søtpotet", M, Produce, ProteinClass.None),
        new("brokkoli", "Broccoli", "Brokkoli", M, Produce, ProteinClass.None),
        new("blomkaal", "Cauliflower", "Blomkål", M, Produce, ProteinClass.None),
        new("hvitkaal", "White cabbage", "Hvitkål", M, Produce, ProteinClass.None),
        new("tomat", "Tomato", "Tomat", C, Produce, ProteinClass.None),
        new("hermetiske_tomater", "Canned tomatoes", "Hermetiske tomater", M, Dry, ProteinClass.None),
        new("tomatpure", "Tomato puree", "Tomatpuré", M, Dry, ProteinClass.None),
        new("agurk", "Cucumber", "Agurk", C, Produce, ProteinClass.None),
        new("paprika", "Bell pepper", "Paprika", C, Produce, ProteinClass.None),
        new("squash", "Zucchini", "Squash", C, Produce, ProteinClass.None),
        new("aubergine", "Eggplant", "Aubergine", C, Produce, ProteinClass.None),
        new("mais", "Corn", "Mais", M, Dry, ProteinClass.None),
        new("erter", "Peas", "Erter", M, Frozen, ProteinClass.None),
        new("spinat", "Spinach", "Spinat", M, Produce, ProteinClass.None),
        new("salat", "Lettuce", "Salat", C, Produce, ProteinClass.None),
        new("sopp", "Mushrooms", "Sopp", M, Produce, ProteinClass.None),
        new("ingefaer", "Ginger", "Ingefær", M, Produce, ProteinClass.None),
        new("chili", "Chili", "Chili", C, Produce, ProteinClass.None),
        new("selleri", "Celery", "Selleri", C, Produce, ProteinClass.None),

        // Frukt
        new("eple", "Apple", "Eple", C, Produce, ProteinClass.None),
        new("banan", "Banana", "Banan", C, Produce, ProteinClass.None),
        new("sitron", "Lemon", "Sitron", C, Produce, ProteinClass.None),
        new("lime", "Lime", "Lime", C, Produce, ProteinClass.None),
        new("appelsin", "Orange", "Appelsin", C, Produce, ProteinClass.None),
        new("jordbaer", "Strawberries", "Jordbær", M, Produce, ProteinClass.None),
        new("blaabaer", "Blueberries", "Blåbær", M, Produce, ProteinClass.None),

        // Kjøtt og fisk
        new("kyllingfilet", "Chicken breast fillet", "Kyllingfilet", M, Meat, ProteinClass.Chicken),
        new("kyllinglaar", "Chicken thighs", "Kyllinglår", M, Meat, ProteinClass.Chicken),
        new("kjottdeig", "Minced meat", "Kjøttdeig", M, Meat, ProteinClass.Meat),
        new("svinekotelett", "Pork chop", "Svinekotelett", C, Meat, ProteinClass.Pork),
        new("bacon", "Bacon", "Bacon", M, Meat, ProteinClass.Pork),
        new("skinke", "Ham", "Skinke", M, Meat, ProteinClass.Pork),
        new("polser", "Sausages", "Pølser", C, Meat, ProteinClass.Meat),
        new("laks", "Salmon", "Laks", M, Meat, ProteinClass.Fish),
        new("torsk", "Cod", "Torsk", M, Meat, ProteinClass.Fish),
        new("reker", "Shrimp", "Reker", M, Meat, ProteinClass.Seafood),
        new("tunfisk", "Tuna", "Tunfisk", M, Dry, ProteinClass.Fish),
        new("fiskepinner", "Fish fingers", "Fiskepinner", C, Frozen, ProteinClass.Fish),

        // Meieri og egg
        new("melk", "Milk", "Melk", V, Dairy, ProteinClass.AnimalProduct),
        new("floete", "Cream", "Fløte", V, Dairy, ProteinClass.AnimalProduct),
        new("matfloete", "Cooking cream", "Matfløte", V, Dairy, ProteinClass.AnimalProduct),
        new("romme", "Sour cream", "Rømme", V, Dairy, ProteinClass.AnimalProduct),
        new("creme_fraiche", "Crème fraîche", "Crème fraîche", V, Dairy, ProteinClass.AnimalProduct),
        new("yoghurt", "Plain yoghurt", "Yoghurt naturell", V, Dairy, ProteinClass.AnimalProduct),
        new("smor", "Butter", "Smør", M, Dairy, ProteinClass.AnimalProduct, IsStaple: true),
        new("ost", "Cheese", "Ost", M, Meat, ProteinClass.AnimalProduct),
        new("mozzarella", "Mozzarella", "Mozzarella", M, Meat, ProteinClass.AnimalProduct),
        new("parmesan", "Parmesan", "Parmesan", M, Meat, ProteinClass.AnimalProduct),
        new("brunost", "Brown cheese", "Brunost", M, Meat, ProteinClass.AnimalProduct),
        new("egg", "Egg", "Egg", C, Dairy, ProteinClass.AnimalProduct),
        new("cottage_cheese", "Cottage cheese", "Cottage cheese", M, Dairy, ProteinClass.AnimalProduct),

        // Tørrvarer og hermetikk
        new("pasta", "Pasta", "Pasta", M, Dry, ProteinClass.None),
        new("spaghetti", "Spaghetti", "Spaghetti", M, Dry, ProteinClass.None),
        new("ris", "Rice", "Ris", M, Dry, ProteinClass.None),
        new("couscous", "Couscous", "Couscous", M, Dry, ProteinClass.None),
        new("nudler", "Noodles", "Nudler", M, Dry, ProteinClass.None),
        new("havregryn", "Rolled oats", "Havregryn", M, Dry, ProteinClass.None),
        new("brod", "Bread", "Brød", C, Dry, ProteinClass.None),
        new("tortillalefser", "Tortilla wraps", "Tortillalefser", C, Dry, ProteinClass.None),
        new("linser", "Lentils", "Linser", M, Dry, ProteinClass.None),
        new("kikerter", "Chickpeas", "Kikerter", M, Dry, ProteinClass.None),
        new("kidneybonner", "Kidney beans", "Kidneybønner", M, Dry, ProteinClass.None),
        new("kokosmelk", "Coconut milk", "Kokosmelk", V, Dry, ProteinClass.None),
        new("buljongterning", "Stock cube", "Buljongterning", C, Dry, ProteinClass.None),
        new("soyasaus", "Soy sauce", "Soyasaus", V, Dry, ProteinClass.None),
        new("sennep", "Mustard", "Sennep", M, Dry, ProteinClass.None),
        new("ketchup", "Ketchup", "Ketchup", M, Dry, ProteinClass.None),
        new("majones", "Mayonnaise", "Majones", M, Dry, ProteinClass.AnimalProduct),
        new("honning", "Honey", "Honning", M, Dry, ProteinClass.AnimalProduct),
        new("sirup", "Syrup", "Sirup", V, Dry, ProteinClass.None),
        new("peanottsmor", "Peanut butter", "Peanøttsmør", M, Dry, ProteinClass.None),

        // Krydder
        new("salt", "Salt", "Salt", M, Dry, ProteinClass.None, IsStaple: true),
        new("sort_pepper", "Black pepper", "Sort pepper", M, Dry, ProteinClass.None, IsStaple: true),
        new("paprikapulver", "Paprika powder", "Paprikapulver", M, Dry, ProteinClass.None),
        new("kanel", "Cinnamon", "Kanel", M, Dry, ProteinClass.None),
        new("spisskummen", "Cumin", "Spisskummen", M, Dry, ProteinClass.None),
        new("oregano", "Oregano", "Oregano", M, Dry, ProteinClass.None),
        new("timian", "Thyme", "Timian", M, Produce, ProteinClass.None),
        new("basilikum", "Basil", "Basilikum", M, Produce, ProteinClass.None),
        new("persille", "Parsley", "Persille", M, Produce, ProteinClass.None),
        new("gressloek", "Chives", "Gressløk", M, Produce, ProteinClass.None),
        new("karri", "Curry powder", "Karri", M, Dry, ProteinClass.None),
        new("muskat", "Nutmeg", "Muskat", M, Dry, ProteinClass.None),
        new("laurbaerblad", "Bay leaf", "Laurbærblad", C, Dry, ProteinClass.None),
        new("kardemomme", "Cardamom", "Kardemomme", M, Dry, ProteinClass.None),

        // Baking
        new("hvetemel", "Wheat flour", "Hvetemel", M, Dry, ProteinClass.None, IsStaple: true),
        new("sukker", "Sugar", "Sukker", M, Dry, ProteinClass.None, IsStaple: true),
        new("melis", "Icing sugar", "Melis", M, Dry, ProteinClass.None),
        new("brunt_sukker", "Brown sugar", "Brunt sukker", M, Dry, ProteinClass.None),
        new("bakepulver", "Baking powder", "Bakepulver", M, Dry, ProteinClass.None, IsStaple: true),
        new("natron", "Baking soda", "Natron", M, Dry, ProteinClass.None, IsStaple: true),
        new("vaniljesukker", "Vanilla sugar", "Vaniljesukker", M, Dry, ProteinClass.None),
        new("kakao", "Cocoa", "Kakao", M, Dry, ProteinClass.None),
        new("gjaer", "Yeast", "Gjær", M, Dry, ProteinClass.None),
        new("maisenna", "Cornstarch", "Maisenna", M, Dry, ProteinClass.None),
        new("sjokolade", "Chocolate", "Sjokolade", M, Dry, ProteinClass.None),

        // Olje, eddik og vann
        new("rapsolje", "Rapeseed oil", "Rapsolje", V, Dry, ProteinClass.None, IsStaple: true),
        new("olje", "Oil", "Olje", V, Dry, ProteinClass.None, IsStaple: true),
        new("noytral_olje", "Neutral oil", "Nøytral olje", V, Dry, ProteinClass.None, IsStaple: true),
        new("margarin", "Margarine", "Margarin", M, Dairy, ProteinClass.None, IsStaple: true),
        new("olivenolje", "Olive oil", "Olivenolje", V, Dry, ProteinClass.None, IsStaple: true),
        new("eddik", "Vinegar", "Eddik", V, Dry, ProteinClass.None, IsStaple: true),
        new("vann", "Water", "Vann", V, Other, ProteinClass.None, IsStaple: true),
    ];
}
