using System.Text;

namespace Dynamo.Modules.Queries;

/// <summary>Writes a result as CSV that Excel opens correctly (UTF-8 with BOM).</summary>
public static class QueryCsv
{
    public static byte[] Write(QueryResult result)
    {
        var sb = new StringBuilder();
        AppendLine(sb, result.Columns.Select(c => c.Name));
        foreach (var row in result.Rows)
            AppendLine(sb, row);
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>A name safe to use as a download file name.</summary>
    public static string FileName(string queryName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(queryName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{(clean.Length == 0 ? "query" : clean)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.csv";
    }

    private static void AppendLine(StringBuilder sb, IEnumerable<string?> cells)
    {
        sb.AppendJoin(',', cells.Select(Quote));
        sb.Append("\r\n");
    }

    // A SQL NULL is an empty field; text with a comma, quote or line break is quoted.
    private static string Quote(string? cell)
    {
        if (string.IsNullOrEmpty(cell))
            return "";
        return cell.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? cell
            : $"\"{cell.Replace("\"", "\"\"")}\"";
    }
}
