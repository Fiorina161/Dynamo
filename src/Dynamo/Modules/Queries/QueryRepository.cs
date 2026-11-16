using System.Text.Json;
using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.Queries;

public sealed class SavedQuery
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Free text; queries sharing one are listed together.</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Name of the Oracle connection preselected when running, or empty to
    /// choose each time. Kept by name, like everything that refers to a
    /// connection: renaming the connection leaves it to be picked again.
    /// </summary>
    public string Connection { get; set; } = "";

    public string Sql { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>The values typed for the bind variables the last time it ran.</summary>
    public Dictionary<string, string> LastParams { get; set; } = [];
}

public sealed class QueryRepository(Db db)
{
    private const string Columns = """
        id, name, category, connection_name AS Connection, sql_text AS Sql,
        notes, last_params AS LastParamsJson
        """;

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Connection { get; set; } = "";
        public string Sql { get; set; } = "";
        public string Notes { get; set; } = "";
        public string LastParamsJson { get; set; } = "{}";

        public SavedQuery ToQuery() => new()
        {
            Id = Id, Name = Name, Category = Category, Connection = Connection,
            Sql = Sql, Notes = Notes, LastParams = ParseParams(LastParamsJson),
        };
    }

    public List<SavedQuery> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<Row>($"SELECT {Columns} FROM saved_query ORDER BY category, name")
            .Select(r => r.ToQuery()).ToList();
    }

    public SavedQuery? Get(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<Row>(
            $"SELECT {Columns} FROM saved_query WHERE id = @id", new { id })?.ToQuery();
    }

    public List<string> Categories()
    {
        using var conn = db.Open();
        return conn.Query<string>(
            "SELECT DISTINCT category FROM saved_query WHERE category <> '' ORDER BY category")
            .ToList();
    }

    /// <summary>The categories collapsed on the list ("" = Uncategorized).</summary>
    public HashSet<string> CollapsedCategories()
    {
        using var conn = db.Open();
        return conn.Query<string>("SELECT category FROM collapsed_category")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Collapses the category, or expands it if it was collapsed.</summary>
    public void ToggleCategory(string category)
    {
        using var conn = db.Open();
        var deleted = conn.Execute(
            "DELETE FROM collapsed_category WHERE category = @category", new { category });
        if (deleted == 0)
            conn.Execute(
                "INSERT INTO collapsed_category (category) VALUES (@category)",
                new { category });
    }

    /// <summary>Inserts when Id is 0, otherwise updates. Returns the id.</summary>
    public long Save(SavedQuery q)
    {
        using var conn = db.Open();
        var args = new { q.Id, q.Name, q.Category, q.Connection, q.Sql, q.Notes };
        if (q.Id != 0)
        {
            conn.Execute("""
                UPDATE saved_query
                   SET name = @Name, category = @Category, connection_name = @Connection,
                       sql_text = @Sql, notes = @Notes
                 WHERE id = @Id
                """, args);
            return q.Id;
        }

        return conn.ExecuteScalar<long>("""
            INSERT INTO saved_query (name, category, connection_name, sql_text, notes)
            VALUES (@Name, @Category, @Connection, @Sql, @Notes);
            SELECT last_insert_rowid();
            """, args);
    }

    public void SaveLastParams(long id, Dictionary<string, string> values)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE saved_query SET last_params = @json WHERE id = @id",
            new { id, json = JsonSerializer.Serialize(values) });
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM saved_query WHERE id = @id", new { id });
    }

    private static Dictionary<string, string> ParseParams(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
