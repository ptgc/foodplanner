namespace Foodplanner.Domain.Catalog;

/// <summary>
/// Best-effort guess of the <see cref="ProteinClass"/> of an ingredient name (T-104), used by the recipe import so a new own
/// ingredient does not start as <see cref="ProteinClass.Unknown"/>. Pure. The answer is only a suggestion the person can change.
/// Order of trust: (1) an exact name or alias in the classified catalog (the caller passes it, see <see cref="CatalogKey"/>),
/// (2) Norwegian and English keywords, whole word or compound ("kyllingfilet", "kremfløte", "revet hvitost"), (3) a small
/// list of plant foods gives <see cref="ProteinClass.None"/>, (4) otherwise <see cref="ProteinClass.Unknown"/>: it never guesses.
/// </summary>
/// <remarks>
/// Decisions. Specific meat beats everything (one kind gives that kind, two kinds give <c>Meat</c>); generic meat words
/// (kjøtt, pølse, kjøttdeig, karbonadedeig, pepperoni, salami) give <c>Meat</c>; fish beats seafood beats dairy/egg; poultry
/// (kylling, kalkun) is <c>Chicken</c>. Plant versions win over the dairy word they contain ("peanøttsmør", "kokosmelk",
/// "havremelk", "soyafløte", "coconut milk": <c>None</c>), and so does a "vegetar/vegansk" marker next to an animal word
/// ("vegetarpølse": <c>None</c>). "Fiskesaus" is <c>Fish</c> and "eggnudler" <c>AnimalProduct</c> (they contain the animal
/// product); "eggplante" is a vegetable. Stocks and broths ("kyllingbuljong") are <c>Unknown</c> (the seed treats stock
/// cubes as plant, a named one may be meat), and so is a bare "deig" (pizzadeig is plant, kjøttdeig is meat).
/// </remarks>
public static class ProteinClassSuggester
{
    private static readonly string[] Markers = ["vegetar", "vegansk", "vegan", "veggie", "plantebasert", "plantbased"];

    private static readonly string[] StockWords = ["buljong", "bouillon", "stock", "broth"];
    private static readonly string[] StockSuffixes = ["kraft", "fond", "buljong"];

    private static readonly string[] PlantDairyPrefixes =
        ["kokos", "havre", "soya", "mandel", "nøtte", "peanøtt", "cashew", "sesam", "solsikke", "hasselnøtt", "erte", "plante", "vegetabilsk"];

    private static readonly string[] PlantDairyRests = ["melk", "fløte", "smør", "drikk", "yoghurt", "yogurt", "ost", "rømme", "krem"];

    private static readonly HashSet<string> PlantQualifiers = new(StringComparer.Ordinal)
    {
        "coconut", "oat", "soy", "soya", "almond", "peanut", "cashew", "cocoa", "rice", "hazelnut", "sesame", "sunflower", "plant", "vegan", "nut",
    };

    private static readonly HashSet<string> DairyEnglish = new(StringComparer.Ordinal) { "milk", "butter", "cream", "cheese", "yogurt", "yoghurt", "drink" };

    private static readonly Rule[] SpecificMeat =
    [
        new(ProteinClass.Chicken, Exact: ["høne"], Prefix: ["kylling", "kalkun", "chicken", "turkey"], Suffix: ["kylling", "kalkun"]),
        new(ProteinClass.Beef, Exact: ["biff"], Prefix: ["storfe", "okse", "biff", "beef", "entrecote", "ribeye", "roastbiff"], Suffix: ["biff"]),
        new(
            ProteinClass.Pork,
            Exact: ["ham", "ribbe"],
            Prefix: ["svin", "bacon", "skinke", "flesk", "pork", "pancetta", "prosciutto", "ribbe", "hamburgerrygg"],
            Suffix: ["skinke", "flesk", "bacon"]),
        new(
            ProteinClass.Lamb,
            Exact: ["lam", "lamb", "får", "mutton"],
            Prefix: ["lamme", "lamb", "fåre", "fårikål", "saue", "mutton"],
            Suffix: []),
    ];

    private static readonly Rule GenericMeat = new(
        ProteinClass.Meat,
        Exact: ["mince", "salami", "sausage", "sausages", "meat", "lever", "liver", "rein", "elg", "hjort", "gås", "vilt", "duck", "goose", "venison"],
        Prefix: ["kjøtt", "pølse", "pølser", "karbonade", "pepperoni", "salami", "chorizo", "mortadella", "leverpostei", "medister", "wiener", "frankfurter", "sausage", "hamburger", "andebryst", "andelår", "andefilet", "andekjøtt", "andelever", "gåse", "hjort", "elg", "reinsdyr", "reinskav", "reinsdyrskav", "vilt", "duck", "goose", "venison"],
        Suffix: ["kjøtt", "pølse", "pølser", "salami", "karbonade", "lever"]);

    private static readonly Rule Fish = new(
        ProteinClass.Fish,
        Exact: ["sei", "ål", "cod", "fish", "tuna", "salmon", "trout", "herring", "haddock", "mackerel", "halibut", "pollock", "surimi"],
        Prefix: ["fisk", "laks", "ørret", "torsk", "seifilet", "seifisk", "hyse", "makrell", "tunfisk", "sild", "kveite", "steinbit", "rødspette", "ansjos", "anchov", "sardin", "kaviar", "rogn", "sjøtunge", "breiflabb", "salmon", "haddock", "mackerel", "tunfisk"],
        Suffix: ["fisk", "laks", "torsk", "ørret", "sild", "kaviar"]);

    private static readonly Rule Seafood = new(
        ProteinClass.Seafood,
        Exact: ["shrimp", "shrimps", "prawn", "prawns", "crab", "lobster", "mussels", "mussel", "oyster", "oysters", "squid", "calamari", "scampi", "reker", "reke"],
        Prefix: ["reke", "reker", "scampi", "krabbe", "hummer", "blåskjell", "kamskjell", "østers", "blekksprut", "kreps", "skjell", "shrimp", "prawn", "lobster"],
        Suffix: ["skjell", "reker", "reke", "krabbe", "hummer"]);

    private static readonly Rule Dairy = new(
        ProteinClass.AnimalProduct,
        Exact: ["ost", "egg", "eggs", "milk", "butter", "cream", "cheese", "yogurt", "yoghurt", "honey", "skyr", "kefir", "kesam", "rømme", "smør", "melk", "fløte"],
        Prefix: ["oste", "melk", "fløte", "rømme", "smør", "egg", "yoghurt", "yogurt", "skyr", "kefir", "kesam", "cottage", "mozzarella", "parmesan", "feta", "cheddar", "gorgonzola", "brie", "camembert", "mascarpone", "ricotta", "halloumi", "philadelphia", "jarlsberg", "norvegia", "gouda", "brunost", "hvitost", "creme", "cream", "majones", "mayonnaise", "honning", "cheese"],
        Suffix: ["ost", "melk", "fløte", "rømme", "smør", "egg", "yoghurt", "yogurt", "mozzarella", "parmesan"]);

    // "Eggplante" starts with "egg" but is an aubergine.
    private static readonly HashSet<string> NotAnimal = new(StringComparer.Ordinal) { "eggplante", "eggeplante", "eggplant", "butternut" };

    private static readonly Rule Plant = new(
        ProteinClass.None,
        Exact: ["ris", "mel", "salt", "sukker", "pepper", "olje", "løk", "eddik", "vann", "tofu", "pasta", "brød", "nudler", "rice", "flour", "sugar", "oil", "onion", "garlic", "tomato", "tomatoes", "water", "vinegar", "carrot", "carrots", "potato", "potatoes", "bread", "noodles", "lime", "lemon", "rismelk", "risdrikk"],
        Prefix: ["tomat", "paprika", "hvitløk", "gulrot", "gulrøt", "potet", "brokkoli", "blomkål", "kål", "salat", "agurk", "squash", "aubergine", "sopp", "champignon", "spinat", "mais", "erter", "ert", "bønner", "linser", "kikert", "soya", "havre", "hvete", "spagetti", "makaroni", "tagliatelle", "penne", "fusilli", "nudler", "basilikum", "persille", "koriander", "timian", "oregano", "rosmarin", "ingefær", "chili", "karri", "kanel", "spisskummen", "gurkemeie", "kardemomme", "muskat", "laurbær", "dill", "mynte", "sitron", "lime", "appelsin", "eple", "epler", "banan", "pære", "jordbær", "blåbær", "bringebær", "mandel", "valnøtt", "cashew", "peanøtt", "pinje", "sesam", "solsikke", "gresskar", "søtpotet", "selleri", "fennikel", "reddik", "rødbet", "ruccola", "mangold", "grønnkål", "rosenkål", "asparges", "avokado", "oliven", "kapers", "ketchup", "sennep", "margarin", "kokos", "couscous", "bulgur", "quinoa", "nøtter", "rosin", "vanilje", "bakepulver", "natron", "gjær", "maisstivelse", "potetmel", "løk", "purre", "vårløk", "sjalottløk", "kålrot", "kålrabi", "tørkede", "kakaosmør", "garlic", "onion", "tomato", "pepper", "oliv", "fullkorn", "tortilla", "brød", "knekkebrød", "lefse", "lasagneplater", "hummus", "tahini", "gnocchi"],
        Suffix: ["sopp", "løk", "mel", "olje", "sukker", "salt", "pepper", "pasta", "eddik", "ris", "puré", "krydder", "frukt", "grønnsaker", "bønner", "linser", "nøtter", "brød"]);

    /// <summary>The lookup key of a name in the classified catalog (lower case, diacritics folded, punctuation removed).</summary>
    public static string CatalogKey(string? name) => string.Join(' ', Words(name));

    /// <summary>Suggests a class for an ingredient name without a catalog.</summary>
    public static ProteinClass Suggest(string? ingredientName) => Suggest(ingredientName, null);

    /// <param name="ingredientName">The text from the recipe ("Grillet kylling", "Torskefilet eller annen kokt").</param>
    /// <param name="catalog">Classified catalog names (all locales) by <see cref="CatalogKey"/>; an exact hit beats the keywords. <c>Unknown</c> entries are ignored.</param>
    public static ProteinClass Suggest(string? ingredientName, IReadOnlyDictionary<string, ProteinClass>? catalog)
    {
        var words = Words(ingredientName);
        if (words.Count == 0)
        {
            return ProteinClass.Unknown;
        }

        if (catalog is not null && catalog.TryGetValue(string.Join(' ', words), out var known) && known != ProteinClass.Unknown)
        {
            return known;
        }

        var marker = words.Any(w => Markers.Any(m => w.StartsWith(m, StringComparison.Ordinal)));
        var stock = words.Any(w => StockWords.Any(s => w.StartsWith(s, StringComparison.Ordinal)) || StockSuffixes.Any(s => w == s || IsCompound(w, s, 2)));

        var specifics = new HashSet<ProteinClass>();
        var generic = false;
        var fish = false;
        var seafood = false;
        var dairy = false;
        var plant = false;

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (NotAnimal.Contains(word) || IsBread(word) || IsPlantDairy(word) || (i > 0 && PlantQualifiers.Contains(words[i - 1]) && DairyEnglish.Contains(word)))
            {
                plant = true;
                continue;
            }

            foreach (var rule in SpecificMeat)
            {
                if (rule.Matches(word))
                {
                    specifics.Add(rule.Class);
                }
            }

            generic |= GenericMeat.Matches(word);
            fish |= Fish.Matches(word);
            seafood |= Seafood.Matches(word);
            dairy |= Dairy.Matches(word);
            plant |= Plant.Matches(word);
        }

        var animal = specifics.Count > 0 || generic || fish || seafood || dairy;
        if (animal && marker)
        {
            return ProteinClass.None;
        }

        if (stock)
        {
            return ProteinClass.Unknown;
        }

        if (specifics.Count == 1)
        {
            return specifics.Single();
        }

        if (specifics.Count > 1)
        {
            return ProteinClass.Meat;
        }

        // A generic meat word ("kjøtt" in "krabbekjøtt") only gives Meat when nothing more specific was found.
        if (fish)
        {
            return ProteinClass.Fish;
        }

        if (seafood)
        {
            return ProteinClass.Seafood;
        }

        if (generic)
        {
            return ProteinClass.Meat;
        }

        if (dairy)
        {
            return ProteinClass.AnimalProduct;
        }

        return plant ? ProteinClass.None : ProteinClass.Unknown;
    }

    /// <summary>Bread, buns, bases and biscuits are plant even when named after a filling ("pølsebrød", "hamburgerbrød", "pizzabunn", "lomper").</summary>
    private static bool IsBread(string word) =>
        BreadWords.Any(b => word == b || IsCompound(word, b, 2));

    private static readonly string[] BreadWords = ["brød", "bunn", "lompe", "lomper", "kjeks"]; // not "bolle": kjøttboller are meat

    private static bool IsPlantDairy(string word)
    {
        foreach (var prefix in PlantDairyPrefixes)
        {
            if (!word.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = word[prefix.Length..];
            if (rest == "is" || PlantDairyRests.Any(r => rest.StartsWith(r, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompound(string word, string suffix, int minRemainder) =>
        word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= minRemainder;

    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var current = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetter(c))
            {
                current.Append(Fold(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static char Fold(char c) => c switch
    {
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'ô' or 'ò' or 'ó' => 'o',
        'î' or 'ï' or 'í' => 'i',
        'ç' => 'c',
        'ü' => 'u',
        'ä' => 'æ',
        'ö' => 'ø',
        'à' or 'á' => 'a',
        _ => c,
    };

    private sealed record Rule(ProteinClass Class, string[] Exact, string[] Prefix, string[] Suffix)
    {
        public bool Matches(string word)
        {
            if (Exact.Contains(word))
            {
                return true;
            }

            foreach (var prefix in Prefix)
            {
                if (word.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var suffix in Suffix)
            {
                // "ost" needs three letters in front ("hvitost"), so "kost", "frost" and "post" are not cheese; "ris" four ("basmatiris").
                var remainder = suffix switch { "ost" => 3, "ris" => 4, _ => 2 };
                if (IsCompound(word, suffix, remainder))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
