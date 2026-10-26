namespace Foodplanner.Domain.Tests.Imports;

/// <summary>Pages shaped like the ones real recipe sites publish. Names and text are made up.</summary>
internal static class JsonLdFixtures
{
    /// <summary>A Norwegian site: one script, a Recipe with HowToStep instructions, ISO times and several images.</summary>
    public const string NorwegianSimple = """
        <!DOCTYPE html>
        <html lang="nb"><head><title>Pannekaker</title>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@type": "Recipe",
          "name": "Perfekte pannekaker",
          "description": "Tynne og gode &amp; enkle.",
          "image": ["https://img.example.test/pannekaker-16x9.jpg", "https://img.example.test/pannekaker-4x3.jpg"],
          "recipeYield": "4 porsjoner",
          "prepTime": "PT10M",
          "cookTime": "PT20M",
          "totalTime": "PT30M",
          "recipeIngredient": ["2,5 dl hvetemel", "5 dl melk", "3 egg", "½ ts salt", "smør til steking"],
          "recipeInstructions": [
            {"@type": "HowToStep", "text": "Visp sammen mel og melk."},
            {"@type": "HowToStep", "text": "Ha i eggene og saltet."},
            {"@type": "HowToStep", "text": "Stek tynne pannekaker i smør."}
          ]
        }
        </script></head><body><h1>Pannekaker</h1></body></html>
        """;

    /// <summary>Yoast-style @graph with a website, an article and the recipe, sections of steps, an ImageObject and a yield list.</summary>
    public const string GraphWithSections = """
        <html><head>
        <script type='application/ld+json' class='yoast-schema-graph'>
        {"@context":"https://schema.org","@graph":[
          {"@type":"WebSite","@id":"https://blog.example.test/#website","url":"https://blog.example.test/","name":"Blog"},
          {"@type":"Article","@id":"https://blog.example.test/lasagne/#article","headline":"Lasagne"},
          {"@type":["Recipe"],"@id":"https://blog.example.test/lasagne/#recipe","name":"Lasagne",
           "image":{"@type":"ImageObject","url":"https://img.example.test/lasagne.jpg","width":1200,"height":800},
           "recipeYield":["6","6 servings"],
           "prepTime":"PT0H25M","cookTime":"PT1H","totalTime":"PT1H25M",
           "recipeIngredient":["500 g kjøttdeig","1 løk","2 fedd hvitløk","1 boks hakkede tomater","9 lasagneplater","200 g ost, revet","salt og pepper"],
           "recipeInstructions":[
             {"@type":"HowToSection","name":"Kjøttsaus","itemListElement":[
               {"@type":"HowToStep","name":"Brun kjøttet","text":"Brun kjøttdeigen i en panne."},
               {"@type":"HowToStep","text":"Ha i løk, hvitløk og tomater. Kok i 20 minutter."}]},
             {"@type":"HowToSection","name":"Sette sammen","itemListElement":[
               {"@type":"HowToStep","text":"Legg lag på lag i en form."},
               {"@type":"HowToStep","text":"Stek i ovnen ved 200 grader i 35 minutter."}]}
           ]}
        ]}
        </script></head></html>
        """;

    /// <summary>A root array, @type as an array, instructions as one HTML string, a yield number and only a total time.</summary>
    public const string ArrayWithHtmlInstructions = """
        <script type="application/ld+json">
        [
          {"@type":"BreadcrumbList","itemListElement":[]},
          {"@type":["Recipe","Article"],"name":"Chili sin carne",
           "recipeYield": 4,
           "totalTime": "PT45M",
           "recipeIngredient": "1 løk\n2 ss olje\n1 boks kidneybønner",
           "recipeInstructions": "<ol><li>Fres løken i oljen.</li><li>Ha i bønner og &amp; krydder.</li></ol><p>Server <b>varm</b>.</p>",
           "image": "https://img.example.test/chili.png"}
        ]
        </script>
        """;

    /// <summary>Two blocks, the first not a recipe; the second wrapped in comment markers with a raw line break inside a string.</summary>
    public const string SloppyBlocks = "<script type=\"application/ld+json\">{\"@type\":\"BreadcrumbList\"}</script>\n"
        + "<script type=\"application/ld+json\"><!--\n{\"@type\":\"Recipe\",\"name\":\"Suppe\nmed brød\",\"recipeYield\":\"Serves 2-3\","
        + "\"recipeIngredient\":[\"1 l vann\",\"2 buljongterninger\",],\"recipeInstructions\":[\"Kok opp vannet.\",\"Ha i terningene.\"]}\n-->\n</script>";

    /// <summary>The Recipe is nested under the page's mainEntity.</summary>
    public const string NestedInWebPage = """
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"WebPage","name":"Side","mainEntity":
          {"@type":"Recipe","name":"Rømmegrøt","recipeYield":"5 porsjoner","cookTime":"PT P",
           "recipeIngredient":["1 l rømme","2 dl hvetemel","1 l melk"],
           "recipeInstructions":[{"@type":"HowToStep","text":"Kok rømmen."},{"@type":"HowToStep","text":"Rør inn melet."}]}}
        </script>
        """;

    public const string ArticleOnly = """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"Article","headline":"Ikke en oppskrift"}</script>
        """;

    public const string BrokenJson = """
        <script type="application/ld+json">{ "@type": "Recipe", "name": </script>
        """;

    public const string NoJsonLd = "<html><body><h1>Pannekaker</h1><p>2 dl melk</p></body></html>";

    /// <summary>A response whose content type is application/ld+json: the document itself, no HTML.</summary>
    public const string PlainDocument = """
        {"@context":"https://schema.org","@type":"Recipe","name":"Vafler","recipeYield":"8",
         "recipeIngredient":["3 dl mel","2 egg"],"recipeInstructions":[{"@type":"HowToStep","text":"Rør."}]}
        """;
}
