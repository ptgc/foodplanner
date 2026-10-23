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
/// comments and string literals. Interpolation holes are lexed as code.
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
        var lexer = new Lexer(text);
        lexer.Run(0, stopAtCloseBrace: false);
        return (lexer.Code.ToString(), lexer.WithStrings.ToString());
    }

    private sealed class Lexer(string text)
    {
        public StringBuilder Code { get; } = new(text.Length);

        public StringBuilder WithStrings { get; } = new(text.Length);

        private void Emit(char c, bool isCode, bool isString)
        {
            var blank = c == '\n' || c == '\r' ? c : ' ';
            Code.Append(isCode ? c : blank);
            WithStrings.Append(isCode || isString ? c : blank);
        }

        private char At(int i) => i < text.Length ? text[i] : '\0';

        /// <summary>
        /// Lexes code from <paramref name="i"/>. With <paramref name="stopAtCloseBrace"/> (an interpolation hole) it
        /// returns the index of the unbalanced closing brace, which it does not consume.
        /// </summary>
        public int Run(int i, bool stopAtCloseBrace)
        {
            var depth = 0;
            while (i < text.Length)
            {
                var c = text[i];
                var next = At(i + 1);

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
                    while (i < text.Length && !(text[i] == '*' && At(i + 1) == '/'))
                    {
                        Emit(text[i++], false, false);
                    }

                    for (var k = 0; k < 2 && i < text.Length; k++)
                    {
                        Emit(text[i++], false, false);
                    }
                }
                else if (c == '"' || ((c == '@' || c == '$') && IsStringStart(i)))
                {
                    i = ReadString(i);
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
                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        if (depth == 0 && stopAtCloseBrace)
                        {
                            return i;
                        }

                        depth--;
                    }

                    Emit(text[i++], true, false);
                }
            }

            return i;
        }

        private bool IsStringStart(int i)
        {
            var j = i;
            while (j < text.Length && (text[j] == '@' || text[j] == '$'))
            {
                j++;
            }

            return j < text.Length && text[j] == '"';
        }

        private int ReadString(int i)
        {
            var verbatim = false;
            var interpolated = false;
            while (text[i] != '"')
            {
                verbatim |= text[i] == '@';
                interpolated |= text[i] == '$';
                Emit(text[i++], true, false); // the @ and $ prefixes
            }

            var quotes = 0;
            while (i + quotes < text.Length && text[i + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3)
            {
                // Raw string literal: ends at the same number of quotes. Interpolation holes in raw strings are not lexed.
                for (var k = 0; k < quotes; k++)
                {
                    Emit(text[i++], false, true);
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
                            Emit(text[i++], false, true);
                        }

                        return i;
                    }

                    Emit(text[i++], false, true);
                }

                return i;
            }

            Emit(text[i++], false, true); // opening quote
            while (i < text.Length)
            {
                var c = text[i];
                if (interpolated && c == '{' && At(i + 1) == '{')
                {
                    Emit(text[i++], false, true);
                    Emit(text[i++], false, true);
                }
                else if (interpolated && c == '{')
                {
                    // An interpolation hole is code: a bypass call inside it is still a call.
                    Emit(text[i++], false, true);
                    i = Run(i, stopAtCloseBrace: true);
                    if (i < text.Length)
                    {
                        Emit(text[i++], false, true); // the closing brace
                    }
                }
                else if (verbatim && c == '"' && At(i + 1) == '"')
                {
                    Emit(text[i++], false, true);
                    Emit(text[i++], false, true);
                }
                else if (!verbatim && c == '\\' && i + 1 < text.Length)
                {
                    Emit(text[i++], false, true);
                    Emit(text[i++], false, true);
                }
                else if (c == '"')
                {
                    Emit(text[i++], false, true);
                    return i;
                }
                else if (!verbatim && c == '\n')
                {
                    return i; // unterminated: do not swallow the rest of the file
                }
                else
                {
                    Emit(text[i++], false, true);
                }
            }

            return i;
        }
    }
}
