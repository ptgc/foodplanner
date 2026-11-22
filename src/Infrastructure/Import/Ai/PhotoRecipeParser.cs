using System.Text.Json;
using Foodplanner.Domain.Imports;

namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// Checks Claude's answer against the schema again, on the server (T-118): the schema steers the model but is not trusted. A body
/// that is not JSON, or whose shape or types differ from the schema, gives null (the call is then "failed", never a 500). Values
/// that are the right type but odd (an unknown purpose, a huge quantity) pass here and are handled line by line by
/// <see cref="PhotoRecipeMapper"/>. Never throws.
/// </summary>
public static class PhotoRecipeParser
{
    public const int MaxItems = 1000;

    public static PhotoRecipe? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Bool(root, "isRecipe", out var isRecipe) || !Bool(root, "readable", out var readable))
            {
                return null;
            }

            if (!isRecipe || !readable)
            {
                return new PhotoRecipe(isRecipe, readable, null, null, null, null, null, null, null, [], [], null, []);
            }

            if (!Text(root, "title", out var title) || !Int(root, "servings", out var servings) || !Int(root, "prepMinutes", out var prep)
                || !Int(root, "cookMinutes", out var cook) || !Text(root, "description", out var description)
                || !Text(root, "sourceBook", out var book) || !Text(root, "rawText", out var raw)
                || !Strings(root, "steps", out var steps) || !Strings(root, "warnings", out var warnings)
                || !Ingredients(root, out var ingredients) || !Picture(root, out var picture))
            {
                return null;
            }

            return new PhotoRecipe(true, true, title, servings, prep, cook, description, book, raw, steps, ingredients, picture, warnings);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Ingredients(JsonElement root, out List<PhotoIngredient> result)
    {
        result = [];
        if (!root.TryGetProperty("ingredients", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxItems)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Text(item, "original", out var original) || !Text(item, "ingredient", out var name)
                || !Text(item, "amountKind", out var kind) || !Decimal(item, "quantity", out var quantity)
                || !Decimal(item, "quantityLow", out var low) || !Text(item, "unit", out var unit) || !Text(item, "purpose", out var purpose)
                || !Bool(item, "isOptional", out var optional) || !Strings(item, "alternatives", out var alternatives)
                || !Text(item, "note", out var note) || name is null)
            {
                return false;
            }

            result.Add(new PhotoIngredient(original, name, kind, quantity, low, unit, purpose, optional, alternatives, note));
        }

        return true;
    }

    private static bool Picture(JsonElement root, out PhotoPicture? picture)
    {
        picture = null;
        if (!root.TryGetProperty("picture", out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind != JsonValueKind.Object || !Int(node, "photoIndex", out var index) || index is null
            || !Int(node, "rotationDegrees", out var rotation) || rotation is null
            || !node.TryGetProperty("box", out var box) || box.ValueKind != JsonValueKind.Object
            || !Double(box, "x", out var x) || !Double(box, "y", out var y) || !Double(box, "w", out var w) || !Double(box, "h", out var h))
        {
            return false;
        }

        picture = new PhotoPicture(index.Value, x, y, w, h, rotation.Value);
        return true;
    }

    private static bool Bool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var node) || node.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = node.GetBoolean();
        return true;
    }

    /// <summary>A string or null; any other type, or a missing property, fails.</summary>
    private static bool Text(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetString();
        return true;
    }

    private static bool Int(JsonElement element, string name, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt32(out var number))
        {
            return false;
        }

        value = number;
        return true;
    }

    private static bool Decimal(JsonElement element, string name, out decimal? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        // A number too large for a decimal is not a usable quantity; it is "odd", not malformed: leave it null so the line is flagged.
        if (node.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = node.TryGetDecimal(out var number) ? number : decimal.MaxValue;
        return true;
    }

    private static bool Double(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out value);
    }

    private static bool Strings(JsonElement element, string name, out List<string> values)
    {
        values = [];
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxItems)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return true;
    }
}
