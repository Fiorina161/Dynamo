using System.Text;

namespace Dynamo.Modules.OracleConnections;

/// <summary>One alias of a tnsnames.ora and the descriptor it stands for.</summary>
public sealed record TnsEntry(string Alias, string Descriptor);

/// <summary>
/// Reads the entries of a tnsnames.ora: <c>ALIAS = (DESCRIPTION = ...)</c>,
/// laid out on one line or many, with # comments anywhere, several aliases
/// sharing one descriptor (<c>A, B = (...)</c>), and quoted values.
/// Each descriptor comes back on a single line without any blank, as Oracle
/// accepts it as a connect string.
/// </summary>
public static class TnsNamesParser
{
    public static List<TnsEntry> Parse(string text)
    {
        var entries = new List<TnsEntry>();
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            SkipBlanksAndComments(text, ref i);
            if (i >= n)
                break;

            // Alias(es), up to the "=".
            var aliasStart = i;
            while (i < n && text[i] != '=' && text[i] != '(' && text[i] != '#' && text[i] != '\n')
                i++;
            if (i >= n || text[i] != '=')
            {
                // Not an entry (a stray word or an IFILE-style line): drop the line.
                SkipLine(text, ref i);
                continue;
            }

            var aliases = text[aliasStart..i]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            i++; // the "="
            SkipBlanksAndComments(text, ref i);
            if (i >= n || text[i] != '(')
            {
                // "ALIAS = something else" (no descriptor): nothing to import.
                SkipLine(text, ref i);
                continue;
            }

            var descriptor = ReadParenthesized(text, ref i);
            if (descriptor is null)
                break; // unbalanced to the end of the file: nothing more is reliable

            foreach (var alias in aliases)
                entries.Add(new TnsEntry(alias, descriptor));
        }

        return entries;
    }

    /// <summary>
    /// Reads from the "(" at <paramref name="i"/> to its matching ")", dropping
    /// comments and every blank outside quotes. Null if it never closes.
    /// </summary>
    private static string? ReadParenthesized(string text, ref int i)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];
            if (c == '#')
            {
                SkipLine(text, ref i);
                continue;
            }

            if (c is '"' or '\'')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0)
                    return null;
                sb.Append(text, i, end - i + 1);
                i = end + 1;
                continue;
            }

            if (!char.IsWhiteSpace(c))
                sb.Append(c);
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;

            i++;
            if (depth == 0)
                return sb.ToString();
        }

        return null;
    }

    private static void SkipBlanksAndComments(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
                i++;
            else if (text[i] == '#')
                SkipLine(text, ref i);
            else
                break;
        }
    }

    private static void SkipLine(string text, ref int i)
    {
        while (i < text.Length && text[i] != '\n')
            i++;
    }
}
