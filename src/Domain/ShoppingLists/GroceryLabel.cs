using System.Globalization;
using System.Text;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// Groups free-text shopping labels and ingredient names (T-110). <see cref="Normalize"/> turns "  Melk ", "melk" and "MELK" into
/// one comparison key: case, whitespace and punctuation are ignored, diacritics are dropped EXCEPT the Norwegian letters (æ, ø, å
/// stay themselves, so "ål" is not "al" and "høne" is not "hone"), and every word gets a simple plural stem: "oster" and "ost"
/// meet, "melk" and "melon" do not. It is a heuristic to group labels and to match a label to an ingredient name; it never
/// changes what a person typed. The key is only ever compared with other keys.
/// </summary>
public static class GroceryLabel
{
    /// <summary>The longest key; a key never grows past the label it was made from (stemming only removes letters).</summary>
    public const int MaxKeyLength = 100;

    private const char CombiningRing = '̊';

    /// <summary>The comparison key of a label or name; empty when nothing is left (only punctuation or spaces).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var cleaned = new StringBuilder(decomposed.Length);
        var previousBase = '\0';
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                // Keep the ring on an a (å); every other accent is dropped (é -> e, ü -> u, ö -> o, ä -> a).
                if (c == CombiningRing && char.ToLowerInvariant(previousBase) == 'a')
                {
                    cleaned.Append(c);
                }

                continue;
            }

            previousBase = c;
            cleaned.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = cleaned.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Stem);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Plural stems, simple on purpose. Norwegian: -er (oster -> ost); English: -ies (berries -> berry), -es after o/s/x/ch/sh
    /// (tomatoes -> tomato), -s (apples -> apple); then a final -e goes (eple, epler and apples all meet at "epl"/"appl"). A stem
    /// keeps at least 3 letters, and -s is left alone after s, u or i (hummus, couscous and ris stay as they are).
    /// </summary>
    private static string Stem(string word)
    {
        var w = word;
        if (w.Length >= 5 && w.EndsWith("ies", StringComparison.Ordinal))
        {
            w = w[..^3] + "y";
        }
        else if (w.Length >= 5 && w.EndsWith("er", StringComparison.Ordinal))
        {
            w = w[..^2];
        }
        else if (w.Length >= 5 && w.EndsWith("es", StringComparison.Ordinal) && IsEsAfter(w[..^2]))
        {
            w = w[..^2];
        }
        else if (w.Length >= 5 && w[^1] == 's' && w[^2] is not ('s' or 'u' or 'i'))
        {
            w = w[..^1];
        }

        if (w.Length >= 4 && w[^1] == 'e')
        {
            w = w[..^1];
        }

        return w;
    }

    private static bool IsEsAfter(string stem) =>
        stem.EndsWith('o') || stem.EndsWith('s') || stem.EndsWith('x') || stem.EndsWith("ch", StringComparison.Ordinal)
        || stem.EndsWith("sh", StringComparison.Ordinal);
}
