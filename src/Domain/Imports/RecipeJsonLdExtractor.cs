using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

/// <summary>A recipe found in a page: the structured fields and the raw JSON of the Recipe node (kept as the import artifact).</summary>
public sealed record JsonLdRecipe(ImportedRecipe Recipe, string RawJson);

/// <summary>
/// Finds a schema.org Recipe in the JSON-LD blocks of an HTML page (or in a JSON-LD document itself): a single object, an
/// array, <c>@graph</c> or nested; <c>recipeInstructions</c> as text, list of text, <c>HowToStep</c> or <c>HowToSection</c>;
/// <c>recipeYield</c> as number, text or list; ISO 8601 times; the image as URL, list or <c>ImageObject</c>. Pure; it never
/// fetches anything.
/// </summary>
public static partial class RecipeJsonLdExtractor
{
    private const int MaxDepth = 8;
    private const int MaxImageUrlLength = 2000;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    [GeneratedRegex(@"<script\b[^>]*\btype\s*=\s*(?:""application/ld\+json[^""]*""|'application/ld\+json[^']*'|application/ld\+json)[^>]*>(?<json>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();

    public static JsonLdRecipe? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        List<string> blocks = trimmed.StartsWith('{') || trimmed.StartsWith('[')
            ? [trimmed]
            : [.. ScriptRegex().Matches(content).Select(m => m.Groups["json"].Value)];

        foreach (var block in blocks)
        {
            var document = TryParse(block);
            if (document is null)
            {
                continue;
            }

            using (document)
            {
                var node = FindRecipe(document.RootElement, 0);
                if (node is { } recipe)
                {
                    return new JsonLdRecipe(ToImported(recipe), recipe.GetRawText());
                }
            }
        }

        return null;
    }

    private static JsonDocument? TryParse(string block)
    {
        var json = block.Trim();
        if (json.StartsWith("<!--", StringComparison.Ordinal))
        {
            json = json[4..];
        }

        if (json.EndsWith("-->", StringComparison.Ordinal))
        {
            json = json[..^3];
        }

        json = json.Trim();
        if (json.StartsWith("<![CDATA[", StringComparison.Ordinal) && json.EndsWith("]]>", StringComparison.Ordinal))
        {
            json = json[9..^3].Trim();
        }

        foreach (var candidate in new[] { json, ReplaceControlCharacters(json) })
        {
            try
            {
                return JsonDocument.Parse(candidate, JsonOptions);
            }
            catch (JsonException)
            {
                // Try the repaired text, then give up on this block.
            }
        }

        return null;
    }

    /// <summary>Real pages often put raw line breaks inside JSON strings, which is invalid JSON; a space is harmless there.</summary>
    private static string ReplaceControlCharacters(string json) =>
        new(json.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static JsonElement? FindRecipe(JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindRecipe(item, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Object:
                if (IsRecipe(element))
                {
                    return element;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if ((property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) && FindRecipe(property.Value, depth + 1) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static bool IsRecipe(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => TypeIsRecipe(type.GetString()),
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && TypeIsRecipe(t.GetString())),
            _ => false,
        };
    }

    private static bool TypeIsRecipe(string? type)
    {
        if (type is null)
        {
            return false;
        }

        var name = type[(type.LastIndexOfAny(['/', ':', '#']) + 1)..];
        return string.Equals(name, "Recipe", StringComparison.OrdinalIgnoreCase);
    }

    private static ImportedRecipe ToImported(JsonElement node)
    {
        var warnings = new List<string>();
        var title = StepTextCleaner.Clean(Text(node, "name"), Recipe.MaxTitleLength);
        var description = StepTextCleaner.Clean(Text(node, "description"), Recipe.MaxDescriptionLength);

        var ingredients = new List<string>();
        foreach (var key in new[] { "recipeIngredient", "ingredients" })
        {
            if (node.TryGetProperty(key, out var value))
            {
                CollectLines(value, ingredients, 0);
                if (ingredients.Count > 0)
                {
                    break;
                }
            }
        }

        var steps = new List<string>();
        if (node.TryGetProperty("recipeInstructions", out var instructions))
        {
            CollectSteps(instructions, steps, 0);
        }

        var prep = Minutes(node, "prepTime");
        var cook = Minutes(node, "cookTime");
        var total = Minutes(node, "totalTime");
        if (prep is null && cook is null && total is not null)
        {
            // Only a total is given: the recipe model has prep and cook, so the total goes to cook (the sum is the same).
            cook = total;
        }

        if (ingredients.Count == 0)
        {
            warnings.Add("The page's recipe has no ingredient list.");
        }

        if (steps.Count == 0)
        {
            warnings.Add("The page's recipe has no instructions.");
        }

        return new ImportedRecipe(
            title.Length == 0 ? null : title,
            description.Length == 0 ? null : description,
            node.TryGetProperty("recipeYield", out var yield) ? Servings(yield, 0) : null,
            prep,
            cook,
            node.TryGetProperty("image", out var image) ? ImageUrl(image, 0) : null,
            ingredients,
            steps,
            warnings);
    }

    private static string? Text(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Minutes(JsonElement node, string key)
    {
        var text = Text(node, key);
        return Durations.FromIso(text) ?? (text is null ? null : Durations.FromText(text));
    }

    /// <summary>Ingredient lines: strings, or objects with a text/name; a string with line breaks gives several.</summary>
    private static void CollectLines(JsonElement value, List<string> lines, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                AddLines(value.GetString(), lines);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectLines(item, lines, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                if ((Text(value, "text") ?? Text(value, "name")) is { } text)
                {
                    AddLines(text, lines);
                }

                break;
        }
    }

    private static void AddLines(string? text, List<string> lines) =>
        lines.AddRange(StepTextCleaner.ToPlainLines(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static void CollectSteps(JsonElement value, List<string> steps, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                steps.AddRange(StepTextCleaner.Split(value.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectSteps(item, steps, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                // HowToSection / ItemList: the steps are in itemListElement. HowToStep: text, else name.
                if (value.TryGetProperty("itemListElement", out var children) && Text(value, "text") is null)
                {
                    CollectSteps(children, steps, depth + 1);
                }
                else if ((Text(value, "text") ?? Text(value, "name")) is { } text)
                {
                    steps.AddRange(StepTextCleaner.Split(text));
                }

                break;
        }
    }

    private static int? Servings(JsonElement value, int depth)
    {
        if (depth > 2)
        {
            return null;
        }

        int? parsed = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                parsed = value.TryGetInt32(out var n) ? n : null;
                break;
            case JsonValueKind.String:
                var match = DigitsRegex().Match(value.GetString() ?? string.Empty);
                parsed = match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (Servings(item, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return parsed is >= Recipe.MinServings and <= Recipe.MaxServings ? parsed : null;
    }

    private static string? ImageUrl(JsonElement value, int depth)
    {
        if (depth > 3)
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ValidImageUrl(value.GetString());
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ImageUrl(item, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Object:
                return value.TryGetProperty("url", out var url) ? ImageUrl(url, depth + 1)
                    : value.TryGetProperty("contentUrl", out var content) ? ImageUrl(content, depth + 1) : null;
            default:
                return null;
        }
    }

    private static string? ValidImageUrl(string? url)
    {
        var trimmed = url?.Trim();
        return trimmed is { Length: > 0 and <= MaxImageUrlLength }
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            ? trimmed
            : null;
    }
}
