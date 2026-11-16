using System.Text.Json;
using Dapper;
using Dynamo.Core;
using Dynamo.Modules.Queries;

namespace Dynamo.Modules.Workplaces;

/// <summary>
/// One query shown in a workspace. The panel owns its query (name and SQL):
/// it is either a copy of a saved query, made when it was added, or written
/// from scratch in the workspace. Changing it never changes a saved query or
/// another panel.
/// </summary>
public sealed class WorkplacePanel
{
    public long Id { get; set; }
    public long WorkplaceId { get; set; }

    /// <summary>The panel's title: the name of its query.</summary>
    public string QueryName { get; set; } = "";

    public string Sql { get; set; } = "";

    /// <summary>The connection this panel runs on; empty = none chosen yet.</summary>
    public string Connection { get; set; } = "";

    /// <summary>Seconds between refreshes; 0 = no auto-refresh.</summary>
    public long RefreshSeconds { get; set; }

    /// <summary>The most rows this panel fetches.</summary>
    public long MaxRows { get; set; } = WorkplaceRepository.DefaultMaxRows;

    public long Position { get; set; }

    /// <summary>Values the saved query had last been run with, when it was copied.</summary>
    public string LastParamsJson { get; set; } = "{}";

    /// <summary>The bind variable names of the query, in order of appearance.</summary>
    public IReadOnlyList<string> Binds => SqlScanner.Analyze(Sql).Binds;

    /// <summary>Seed values for the shared inputs (see <see cref="Workplace.SharedBinds"/>).</summary>
    public Dictionary<string, string> LastParams
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(LastParamsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

public sealed class Workplace
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string BindsJson { get; set; } = "{}";
    public long PanelCount { get; set; }
    public List<WorkplacePanel> Panels { get; set; } = [];

    /// <summary>The bind values shared by the panels, by bind name.</summary>
    public Dictionary<string, string> Binds
    {
        get
        {
            try
            {
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(BindsJson) ?? [],
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The distinct bind names of all the panels (case-insensitive), each with
    /// its current value: the workspace's own, else what the first panel using
    /// it was copied with.
    /// </summary>
    public List<(string Name, string Value)> SharedBinds()
    {
        var saved = Binds;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var panel in Panels)
        {
            var last = panel.LastParams;
            foreach (var bind in panel.Binds)
            {
                if (!seen.Add(bind))
                    continue;
                var value = saved.TryGetValue(bind, out var v) ? v
                    : last.TryGetValue(bind, out var l) ? l : "";
                result.Add((bind, value));
            }
        }
        return result;
    }
}

public sealed class WorkplaceRepository(Db db)
{
    public const int MaxSeconds = 3600;
    public const int DefaultMaxRows = QueryRunner.DefaultMaxRows;
    public const int MaxRowsLimit = QueryRunner.MaxRowsLimit;

    private const string PanelSelect = """
        SELECT p.id AS Id, p.watch_id AS WorkplaceId, p.name AS QueryName,
               p.sql_text AS Sql, p.last_params AS LastParamsJson,
               p.connection_name AS Connection, p.refresh_seconds AS RefreshSeconds,
               p.max_rows AS MaxRows, p.position AS Position
          FROM watch_panel p
        """;

    public List<Workplace> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<Workplace>("""
            SELECT w.id, w.name,
                   (SELECT COUNT(*) FROM watch_panel WHERE watch_id = w.id) AS PanelCount
              FROM watch w ORDER BY w.name
            """).ToList();
    }

    /// <summary>The workspace with its panels in order, or null.</summary>
    public Workplace? Get(long id)
    {
        using var conn = db.Open();
        var workplace = conn.QuerySingleOrDefault<Workplace>(
            "SELECT id, name, binds AS BindsJson FROM watch WHERE id = @id", new { id });
        if (workplace is null)
            return null;
        workplace.Panels = conn.Query<WorkplacePanel>(
            $"{PanelSelect} WHERE p.watch_id = @id ORDER BY p.position, p.id", new { id }).ToList();
        workplace.PanelCount = workplace.Panels.Count;
        return workplace;
    }

    public WorkplacePanel? GetPanel(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<WorkplacePanel>($"{PanelSelect} WHERE p.id = @id", new { id });
    }

    public long Create(string name)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>(
            "INSERT INTO watch (name) VALUES (@name); SELECT last_insert_rowid();", new { name });
    }

    public void Rename(long id, string name)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch SET name = @name WHERE id = @id", new { id, name });
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM watch WHERE id = @id", new { id });
    }

    public bool NameTaken(string name, long exceptId)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM watch WHERE name = @name AND id <> @exceptId",
            new { name, exceptId }) > 0;
    }

    /// <summary>
    /// Adds a panel per saved-query id, at the end, in the order given. Each
    /// panel gets its own COPY of the query (name, SQL, connection, the values
    /// last used): the saved query can change or disappear without touching
    /// it, and the other way round. The same query may be copied twice;
    /// unknown ids are ignored. Returns how many panels were added.
    /// </summary>
    public int AddCopies(long workplaceId, IEnumerable<long> savedQueryIds)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var added = 0;
        foreach (var queryId in savedQueryIds)
            added += conn.Execute("""
                INSERT INTO watch_panel
                    (watch_id, name, sql_text, last_params, connection_name, position)
                SELECT @workplaceId, q.name, q.sql_text, q.last_params, q.connection_name,
                       (SELECT COALESCE(MAX(position), 0) + 1
                          FROM watch_panel WHERE watch_id = @workplaceId)
                  FROM saved_query q WHERE q.id = @queryId
                """, new { workplaceId, queryId }, tx);
        tx.Commit();
        return added;
    }

    /// <summary>A panel with a query written in the workspace itself. Returns its id.</summary>
    public long AddNew(long workplaceId, string name, string sql, string connection)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>("""
            INSERT INTO watch_panel (watch_id, name, sql_text, connection_name, position)
            VALUES (@workplaceId, @name, @sql, @connection,
                    (SELECT COALESCE(MAX(position), 0) + 1
                       FROM watch_panel WHERE watch_id = @workplaceId));
            SELECT last_insert_rowid();
            """, new { workplaceId, name, sql, connection });
    }

    /// <summary>Changes the panel's own query; nothing else is affected.</summary>
    public void SetPanelQuery(long id, string name, string sql)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch_panel SET name = @name, sql_text = @sql WHERE id = @id",
            new { id, name, sql });
    }

    public void RemovePanel(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM watch_panel WHERE id = @id", new { id });
    }

    /// <summary>
    /// Saves a panel's refresh interval (0..3600 s) and, when given, its
    /// connection and row limit (1..10,000); a null connection or limit leaves
    /// it as it is.
    /// </summary>
    public void SetPanel(long id, string? connection, long seconds, long? maxRows = null)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE watch_panel
               SET connection_name = COALESCE(@connection, connection_name),
                   refresh_seconds = @seconds,
                   max_rows = COALESCE(@maxRows, max_rows)
             WHERE id = @id
            """, new
        {
            id, connection,
            seconds = Math.Clamp(seconds, 0, MaxSeconds),
            maxRows = maxRows is { } m ? Math.Clamp(m, 1, MaxRowsLimit) : (long?)null,
        });
    }

    /// <summary>
    /// Moves a panel one place earlier (delta -1) or later (+1) among the
    /// panels of its workspace; at either end nothing changes.
    /// </summary>
    public void MovePanel(long id, int delta)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var workplaceId = conn.ExecuteScalar<long?>(
            "SELECT watch_id FROM watch_panel WHERE id = @id", new { id }, tx);
        if (workplaceId is null)
            return;
        var order = conn.Query<long>(
            "SELECT id FROM watch_panel WHERE watch_id = @workplaceId ORDER BY position, id",
            new { workplaceId }, tx).ToList();
        var from = order.IndexOf(id);
        var to = from + Math.Sign(delta);
        if (from >= 0 && to >= 0 && to < order.Count)
        {
            (order[from], order[to]) = (order[to], order[from]);
            for (var i = 0; i < order.Count; i++)
                conn.Execute("UPDATE watch_panel SET position = @position WHERE id = @id",
                    new { id = order[i], position = i + 1 }, tx);
        }
        tx.Commit();
    }

    /// <summary>
    /// A copy of a workspace under a new name: the same panels (each with its
    /// own copy of the query, connection, interval, row limit, order) and the
    /// same shared values. Returns the new id, or 0 when the original does not
    /// exist.
    /// </summary>
    public long Duplicate(long id, string name)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var copy = conn.ExecuteScalar<long?>("""
            INSERT INTO watch (name, binds) SELECT @name, binds FROM watch WHERE id = @id;
            SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;
            """, new { id, name }, tx);
        if (copy is null)
            return 0;
        conn.Execute("""
            INSERT INTO watch_panel
                (watch_id, name, sql_text, last_params, connection_name,
                 refresh_seconds, max_rows, position)
            SELECT @copy, name, sql_text, last_params, connection_name,
                   refresh_seconds, max_rows, position
              FROM watch_panel WHERE watch_id = @id
            """, new { copy, id }, tx);
        tx.Commit();
        return copy.Value;
    }

    public void SaveBinds(long workplaceId, IReadOnlyDictionary<string, string> values)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch SET binds = @json WHERE id = @workplaceId",
            new { workplaceId, json = JsonSerializer.Serialize(values) });
    }
}
