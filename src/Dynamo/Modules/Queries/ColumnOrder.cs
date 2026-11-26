namespace Dynamo.Modules.Queries;

/// <summary>
/// The order of the columns of a panel's result as the user left it (dragged by their headers): a list of
/// column names, kept with the panel. The server applies it to every result, so each refresh already comes in that
/// order, and the CSV export follows it too (what is on screen is what is exported). A name that is no longer in the
/// result is ignored, and a column that is not in the list (a new one) goes after the others in the query's own order.
/// </summary>
public static class ColumnOrder
{
    public const int MaxNames = 500;
    public const int MaxNameLength = 200;

    /// <summary>The result with its columns (and every row) in the saved order; the same object when nothing changes.</summary>
    public static QueryResult Apply(QueryResult result, IReadOnlyList<string>? order)
    {
        if (order is null || order.Count == 0 || result.Columns.Count < 2)
            return result;

        var used = new bool[result.Columns.Count];
        var permutation = new List<int>(result.Columns.Count);
        foreach (var name in order)
        {
            for (var i = 0; i < result.Columns.Count; i++)
            {
                if (used[i] || !string.Equals(result.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                used[i] = true;
                permutation.Add(i);
                break;
            }
        }
        for (var i = 0; i < used.Length; i++)
        {
            if (!used[i])
                permutation.Add(i);
        }
        if (permutation.Select((source, place) => source == place).All(same => same))
            return result;

        var columns = permutation.Select(i => result.Columns[i] with { Source = i }).ToList();
        var rows = result.Rows.Select(row => permutation.Select(i => row[i]).ToArray()).ToList();
        return result with { Columns = columns, Rows = rows };
    }
}
