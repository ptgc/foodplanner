using System.Net;
using System.Text.RegularExpressions;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

/// <summary>
/// Turns scraped or pasted text into plain text: HTML tags removed, entities decoded, whitespace collapsed, control
/// characters dropped. Used for steps, titles and descriptions.
/// </summary>
public static partial class StepTextCleaner
{
    [GeneratedRegex(@"<\s*br\s*/?\s*>|<\s*/\s*(?:p|li|div|h[1-6]|tr|ul|ol)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<script\b.*?</script\s*>|<style\b.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpacesRegex();

    [GeneratedRegex(@"^\s*(?:[-•*·▪●■□☐✓○]\s+|(?:(?:steg|step|trinn|punkt)\s*)?\d{1,2}\s*[.):]\s+|(?:(?:steg|step|trinn)\s*)\d{1,2}\s*\p{Pd}\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarkerRegex();

    /// <summary>HTML to text with a newline at each block end; entities decoded; no tags. Lines keep their order.</summary>
    public static string ToPlainLines(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = ScriptRegex().Replace(html, " ");
        text = BlockEndRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace((char)0x00A0, ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        return new string(text.Where(c => c == '\n' || !char.IsControl(c)).ToArray());
    }

    /// <summary>One cleaned line of text (no list marker or step number in front); empty when nothing is left.</summary>
    public static string Clean(string? text, int maxLength = RecipeStep.MaxTextLength)
    {
        var plain = ToPlainLines(text).Replace('\n', ' ');
        plain = InlineSpacesRegex().Replace(plain, " ").Trim();
        plain = LeadingMarkerRegex().Replace(plain, string.Empty).Trim();
        return plain.Length <= maxLength ? plain : plain[..maxLength].TrimEnd();
    }

    /// <summary>Splits a block (HTML or plain text) into cleaned, non-empty steps, one per line.</summary>
    public static IReadOnlyList<string> Split(string? block) =>
        [.. ToPlainLines(block).Split('\n').Select(line => Clean(line)).Where(line => line.Length > 0)];
}
