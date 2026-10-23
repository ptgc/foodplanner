using System.Text;

namespace Foodplanner.Architecture.Tests;

/// <summary>A C# source file in two views, both with comments blanked and line numbers preserved.</summary>
/// <param name="Path">Solution-relative path with forward slashes.</param>
/// <param name="Code">String and char literal contents blanked too, so text in a literal never counts as code.</param>
/// <param name="CodeWithStrings">String literals kept, for rules that must see a column name written as a string.</param>
public sealed record SourceFile(string Path, string Code, string CodeWithStrings)
{
    public static SourceFile FromText(string path, string text)
    {
        var (code, withStrings) = SourceScanner.Strip(text);
        return new SourceFile(path, code, withStrings);
    }

    public int LineOf(int index) => 1 + Code.AsSpan(0, index).Count('\n');
}

/// <summary>
/// A small C# lexer, not a parser. It is enough to ask "does this identifier appear in code" without being fooled by
/// comments and string literals. Interpolation holes inside strings are treated as string text.
/// </summary>
public static class SourceScanner
{
    public static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Foodplanner.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }

    /// <summary>Every .cs file under the given solution-relative folders, except build output.</summary>
    public static List<SourceFile> Load(params string[] folders)
    {
        var root = SolutionRoot();
        var files = new List<SourceFile>();
        foreach (var folder in folders)
        {
            foreach (var path in Directory.EnumerateFiles(System.IO.Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(SourceFile.FromText(relative, File.ReadAllText(path)));
            }
        }

        return files;
    }

    public static (string Code, string WithStrings) Strip(string text)
    {
        var code = new StringBuilder(text.Length);
        var withStrings = new StringBuilder(text.Length);

        void Emit(char c, bool isCode, bool isString)
        {
            var blank = c == '\n' || c == '\r' ? c : ' ';
            code.Append(isCode ? c : blank);
            withStrings.Append(isCode || isString ? c : blank);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    Emit(text[i++], false, false);
                }
            }
            else if (c == '/' && next == '*')
            {
                Emit(text[i++], false, false);
                Emit(text[i++], false, false);
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    Emit(text[i++], false, false);
                }

                for (var k = 0; k < 2 && i < text.Length; k++)
                {
                    Emit(text[i++], false, false);
                }
            }
            else if (c == '"' || ((c == '@' || c == '$') && IsStringStart(text, i)))
            {
                i = ReadString(text, i, Emit);
            }
            else if (c == '\'')
            {
                // Char literal such as 'a', '\'' or '"'.
                Emit(text[i++], false, false);
                while (i < text.Length && text[i] != '\'' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        Emit(text[i++], false, false);
                    }

                    Emit(text[i++], false, false);
                }

                if (i < text.Length && text[i] == '\'')
                {
                    Emit(text[i++], false, false);
                }
            }
            else
            {
                Emit(text[i++], true, false);
            }
        }

        return (code.ToString(), withStrings.ToString());
    }

    private static bool IsStringStart(string text, int i)
    {
        var j = i;
        while (j < text.Length && (text[j] == '@' || text[j] == '$'))
        {
            j++;
        }

        return j < text.Length && text[j] == '"';
    }

    private static int ReadString(string text, int i, Action<char, bool, bool> emit)
    {
        var verbatim = false;
        while (text[i] != '"')
        {
            verbatim |= text[i] == '@';
            emit(text[i++], true, false); // the @ and $ prefixes
        }

        var quotes = 0;
        while (i + quotes < text.Length && text[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // Raw string literal: ends at the same number of quotes.
            for (var k = 0; k < quotes; k++)
            {
                emit(text[i++], false, true);
            }

            while (i < text.Length)
            {
                var run = 0;
                while (i + run < text.Length && text[i + run] == '"')
                {
                    run++;
                }

                if (run >= quotes)
                {
                    for (var k = 0; k < quotes; k++)
                    {
                        emit(text[i++], false, true);
                    }

                    return i;
                }

                emit(text[i++], false, true);
            }

            return i;
        }

        emit(text[i++], false, true); // opening quote
        while (i < text.Length)
        {
            var c = text[i];
            if (verbatim && c == '"' && i + 1 < text.Length && text[i + 1] == '"')
            {
                emit(text[i++], false, true);
                emit(text[i++], false, true);
            }
            else if (!verbatim && c == '\\' && i + 1 < text.Length)
            {
                emit(text[i++], false, true);
                emit(text[i++], false, true);
            }
            else if (c == '"')
            {
                emit(text[i++], false, true);
                return i;
            }
            else if (!verbatim && c == '\n')
            {
                return i; // unterminated: do not swallow the rest of the file
            }
            else
            {
                emit(text[i++], false, true);
            }
        }

        return i;
    }
}
