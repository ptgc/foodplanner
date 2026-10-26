using System.Text;

namespace Foodplanner.Domain.Imports;

/// <summary>
/// A catalog ingredient the matcher may map to: global or the household's own (the caller loads them through the
/// tenant-filtered set, so another household's ingredients are never candidates). <see cref="Names"/> holds every name in
/// every locale (nb, en, or the one name a household typed); <see cref="DisplayName"/> is shown to the person.
/// </summary>
public sealed record IngredientCandidate(Guid Id, string DisplayName, IReadOnlyList<string> Names, bool IsOwn);

public sealed record IngredientSuggestion(Guid IngredientId, string Name, double Confidence);

/// <summary><see cref="IngredientId"/> is null when nothing reached <see cref="IngredientMatcher.Threshold"/> (flagged for manual mapping).</summary>
public sealed record IngredientMatchResult(Guid? IngredientId, string? Name, double Confidence, IReadOnlyList<IngredientSuggestion> Suggestions);

/// <summary>
/// Maps ingredient text from a recipe to a catalog ingredient. Tiers, best score wins: exact normalized name (1.0), the same
/// after light Norwegian/English plural stemming and folding of æøå (0.95; "gulrøtter" = "Gulrot"), the same with spaces
/// ignored (0.93; "rød løk" = "Rødløk"), the words of the shorter of text and catalog name all found in the longer (0.7 to 0.9; "rød paprika" finds
/// "Paprika", "yoghurt" finds "Yoghurt naturell"; two such catalog names tie and stay unmatched), and a close spelling (edit
/// distance at most 2, names of 5+ letters, scaled by length: one slip needs a 7+ letter word to pass). The text
/// is also tried without descriptors such as "fersk" or "hakkede" (x0.95). Below <see cref="Threshold"/>, or when two
/// different ingredients score the same without being an exact match, the text stays unmatched and the best candidates are
/// returned as suggestions. At an equal score the household's own ingredient wins over a global one.
/// </summary>
public sealed class IngredientMatcher
{
    public const double Threshold = 0.8;

    private const double SuggestionFloor = 0.5;
    private const int MaxSuggestions = 3;
    private const double TieMargin = 0.01;
    private const double ExactTierFloor = 0.95;

    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal)
    {
        "liten", "lite", "små", "stor", "store", "mellomstor", "mellomstore", "fersk", "ferske", "frisk", "friske", "finhakket",
        "finhakkede", "hakket", "hakkede", "grovhakket", "revet", "revne", "finrevet", "skivet", "skårne", "oppskåret", "kokt",
        "kokte", "ristet", "ristede", "myk", "myke", "smeltet", "tørket", "tørkede", "frosset", "frosne", "moden", "modne", "litt",
        "noen", "small", "large", "medium", "big", "fresh", "chopped", "minced", "grated", "shredded", "diced", "sliced",
        "melted", "softened", "dried", "frozen", "ripe", "finely", "roughly", "freshly", "peeled", "boneless", "skinless",
        "cooked", "raw", "optional",
    };

    private readonly List<Prepared> _candidates;

    public IngredientMatcher(IEnumerable<IngredientCandidate> candidates)
    {
        _candidates = [.. candidates.Select(c => new Prepared(c, [.. c.Names.Select(Forms.Of).Where(f => f.Tokens.Length > 0)]))];
    }

    public IngredientMatchResult Match(string? text)
    {
        var variants = new List<(Forms Forms, double Factor)>();
        var full = Forms.Of(text ?? string.Empty);
        if (full.Tokens.Length == 0)
        {
            return new IngredientMatchResult(null, null, 0, []);
        }

        variants.Add((full, 1.0));
        var stripped = Forms.FromTokens([.. full.Tokens.Where(t => !Descriptors.Contains(t))]);
        if (stripped.Tokens.Length > 0 && stripped.Tokens.Length < full.Tokens.Length)
        {
            variants.Add((stripped, 0.95));
        }

        var scored = new List<(Prepared Candidate, double Score)>();
        foreach (var candidate in _candidates)
        {
            var best = 0.0;
            foreach (var (forms, factor) in variants)
            {
                foreach (var name in candidate.Names)
                {
                    best = Math.Max(best, Math.Round(Score(forms, name) * factor, 4));
                }
            }

            if (best >= SuggestionFloor)
            {
                scored.Add((candidate, best));
            }
        }

        var ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Candidate.Source.IsOwn)
            .ThenBy(s => s.Candidate.Source.DisplayName, StringComparer.Ordinal)
            .ThenBy(s => s.Candidate.Source.Id)
            .ToList();
        if (ordered.Count == 0)
        {
            return new IngredientMatchResult(null, null, 0, []);
        }

        var top = ordered[0];
        var ambiguous = top.Score < ExactTierFloor && ordered.Count > 1 && top.Score - ordered[1].Score < TieMargin;
        if (top.Score >= Threshold && !ambiguous)
        {
            return new IngredientMatchResult(
                top.Candidate.Source.Id, top.Candidate.Source.DisplayName, Round(top.Score),
                Suggest(ordered.Skip(1)));
        }

        return new IngredientMatchResult(null, null, 0, Suggest(ordered));
    }

    private static IReadOnlyList<IngredientSuggestion> Suggest(IEnumerable<(Prepared Candidate, double Score)> ordered) =>
        [.. ordered.Take(MaxSuggestions).Select(s => new IngredientSuggestion(s.Candidate.Source.Id, s.Candidate.Source.DisplayName, Round(s.Score)))];

    private static double Round(double score) => Math.Round(score, 2, MidpointRounding.AwayFromZero);

    private static double Score(Forms text, Forms name)
    {
        if (text.Plain == name.Plain)
        {
            return 1.0;
        }

        if (text.Stemmed == name.Stemmed)
        {
            return 0.95;
        }

        if (text.Compact == name.Compact || text.Sorted == name.Sorted)
        {
            return 0.93;
        }

        var best = 0.0;
        var (small, large) = text.StemmedTokens.Length <= name.StemmedTokens.Length
            ? (text.StemmedTokens, name.StemmedTokens)
            : (name.StemmedTokens, text.StemmedTokens);
        if (small.Length < large.Length && small.All(t => large.Contains(t)))
        {
            best = 0.7 + 0.2 * small.Length / large.Length;
        }

        var longest = Math.Max(text.Compact.Length, name.Compact.Length);
        if (name.Compact.Length >= 5 && text.Compact.Length >= 5 && Math.Abs(text.Compact.Length - name.Compact.Length) <= 2)
        {
            var distance = Levenshtein(text.Compact, name.Compact);
            if (distance <= 2)
            {
                best = Math.Max(best, (1.0 - (double)distance / longest) * 0.95);
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private sealed record Prepared(IngredientCandidate Source, Forms[] Names);

    /// <summary>The normalized shapes of a string that the tiers compare.</summary>
    private sealed record Forms(string[] Tokens, string Plain, string[] StemmedTokens, string Stemmed, string Compact, string Sorted)
    {
        private static readonly string[] Suffixes = ["ene", "ers", "er", "en", "et", "es", "e", "s"];

        public static Forms Of(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text.ToLowerInvariant())
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return FromTokens(sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static Forms FromTokens(string[] tokens)
        {
            var stemmed = tokens.Select(Stem).ToArray();
            return new Forms(tokens, string.Join(' ', tokens), stemmed, string.Join(' ', stemmed), string.Concat(stemmed), string.Join(' ', stemmed.Order(StringComparer.Ordinal)));
        }

        private static string Stem(string token)
        {
            var word = token.Replace("ø", "o").Replace("å", "a").Replace("æ", "ae").Replace("é", "e").Replace("è", "e").Replace("ü", "u").Replace("ö", "o");
            var changed = true;
            while (changed && word.Length > 3)
            {
                changed = false;
                foreach (var suffix in Suffixes)
                {
                    if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        word = word[..^suffix.Length];
                        changed = true;
                        break;
                    }
                }
            }

            return word.Length > 3 && word[^1] == word[^2] ? word[..^1] : word;
        }
    }
}
