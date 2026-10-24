using System.Globalization;

namespace Foodplanner.Domain.Localization;

/// <summary>
/// The locales the product has text for (Q-013): Norwegian bokmål and English. English is the default and the fallback.
/// A locale is a lower-case language code. Adding one needs no schema change (translation rows carry the code as data):
/// add it to <see cref="Supported"/> and to the seed data files, and teach <see cref="FromLanguageTag"/> its tags.
/// </summary>
public static class Locales
{
    public const string English = "en";
    public const string Norwegian = "nb";

    /// <summary>The fallback locale: shown when a requested locale has no text, and when none is requested.</summary>
    public const string Default = English;

    public static IReadOnlyList<string> Supported { get; } = [Norwegian, English];

    /// <summary>
    /// Picks the locale for an <c>Accept-Language</c> header value: the supported language with the highest q-value
    /// (ties by position), <c>nb</c>/<c>no</c>/<c>nn</c> (with any region) giving <c>nb</c> and <c>en</c> giving
    /// <c>en</c>; q=0 means "not acceptable". No header, a malformed one or no supported language gives
    /// <see cref="Default"/>. Never throws.
    /// </summary>
    public static string Negotiate(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return Default;
        }

        string? best = null;
        var bestQuality = 0m;
        foreach (var part in acceptLanguage.Split(','))
        {
            var pieces = part.Split(';');
            var locale = FromLanguageTag(pieces[0]);
            if (locale is null)
            {
                continue;
            }

            var quality = 1m;
            foreach (var parameter in pieces.Skip(1))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    quality = decimal.TryParse(pair[1].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var q)
                        && q is >= 0 and <= 1 ? q : 0m;
                }
            }

            if (quality > bestQuality)
            {
                best = locale;
                bestQuality = quality;
            }
        }

        return best ?? Default;
    }

    /// <summary>The supported locale for one language tag such as <c>nb-NO</c>, <c>no</c> or <c>en-GB</c>; null when none.</summary>
    public static string? FromLanguageTag(string tag)
    {
        var language = tag.Trim().Split('-', '_')[0].ToLowerInvariant();
        return language switch
        {
            "nb" or "no" or "nn" => Norwegian,
            "en" => English,
            _ => null,
        };
    }
}
