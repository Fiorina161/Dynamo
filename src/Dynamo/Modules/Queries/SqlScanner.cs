using System.Text;
using System.Text.RegularExpressions;

namespace Dynamo.Modules.Queries;

/// <summary>
/// Reads a SQL text once, skipping comments and string literals, to find its
/// bind variables and to check that it is a single SELECT.
/// </summary>
public static class SqlScanner
{
    /// <param name="Sql">The text to execute: the input without its closing semicolon.</param>
    /// <param name="Binds">Distinct bind variables (:name) in order of appearance.</param>
    /// <param name="Error">Why it cannot be run, or null when it can.</param>
    public sealed record Analysis(string Sql, IReadOnlyList<string> Binds, string? Error);

    private static readonly Regex FirstWord =
        new(@"^[\s(]*([A-Za-z]+)", RegexOptions.Compiled);

    public static Analysis Analyze(string text)
    {
        var binds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var code = new StringBuilder();   // the SQL without comments and literal contents
        var semicolons = new List<int>(); // their positions in `text`
        var n = text.Length;

        for (var i = 0; i < n; i++)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < n && text[i] != '\n')
                    i++;
                code.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 1;
                code.Append(' ');
            }
            else if ((c == 'q' || c == 'Q') && next == '\'' && i + 2 < n && !IsWordChar(i, text))
            {
                // Alternative quoting: q'[ ... ]', q'{ ... }', q'! ... !'
                var open = text[i + 2];
                var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
                var end = text.IndexOf($"{close}'", i + 3, StringComparison.Ordinal);
                i = end < 0 ? n : end + 1;
                code.Append("''");
            }
            else if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (text[i] == '\'' && i + 1 < n && text[i + 1] == '\'')
                        i += 2;
                    else if (text[i] == '\'')
                        break;
                    else
                        i++;
                }

                code.Append("''");
            }
            else if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                i = end < 0 ? n : end;
                code.Append('x');
            }
            else if (c == ':' && (char.IsLetter(next) || next == '_'))
            {
                var start = i + 1;
                i = start;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' or '#'))
                    i++;
                var name = text[start..i];
                i--;
                if (seen.Add(name))
                    binds.Add(name);
                code.Append(":b");
            }
            else
            {
                if (c == ';')
                    semicolons.Add(i);
                code.Append(c);
            }
        }

        var codeText = code.ToString();

        // What the statement is comes first: "only SELECT" is the better
        // message for a script such as BEGIN ...; END.
        var word = FirstWord.Match(codeText);
        if (!word.Success)
            return new Analysis(text, binds, "The query is empty.");

        var keyword = word.Groups[1].Value.ToUpperInvariant();
        if (keyword is not ("SELECT" or "WITH"))
            return new Analysis(text, binds,
                $"Only SELECT queries are allowed (this one starts with {keyword}).");

        // One closing semicolon is tolerated (Oracle itself refuses it).
        var sql = text;
        var trailing = semicolons.Count == 1
            && codeText[(codeText.IndexOf(';') + 1)..].Trim().Length == 0;
        if (trailing)
            sql = text.Remove(semicolons[0], 1);
        else if (semicolons.Count > 0)
            return new Analysis(text, binds, "Only one statement at a time (found a “;” inside).");

        return new Analysis(sql.TrimEnd(), binds, null);
    }

    private static bool IsWordChar(int index, string text) =>
        index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] is '_' or '$' or '#');
}
