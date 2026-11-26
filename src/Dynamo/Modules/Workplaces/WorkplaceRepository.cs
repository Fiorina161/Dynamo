using System.Text.Json;
using Dapper;
using Dynamo.Core;
using Dynamo.Modules.Queries;

namespace Dynamo.Modules.Workplaces;

/// <summary>
/// One query shown in a workspace. The panel owns its query (name and SQL):
/// it is either written in the workspace or copied from a panel of another
/// workspace (an import). Changing it never changes another panel.
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

    /// <summary>
    /// How many rows of the result show before it scrolls (its height); 0 = the
    /// default number of rows from Settings.
    /// </summary>
    public long ViewRows { get; set; }

    public long Position { get; set; }

    /// <summary>Shown as its title line only (it keeps refreshing).</summary>
    public bool Collapsed { get; set; }

    /// <summary>A <see cref="PanelKinds"/> value: an ordinary query, or the DBA sessions panel.</summary>
    public string Kind { get; set; } = PanelKinds.Query;

    public bool IsSessions => Kind == PanelKinds.Sessions;

    /// <summary>The columns' names in the order the user left them (JSON); empty = the query's own order.</summary>
    public string ColumnOrderJson { get; set; } = "";

    /// <summary>The saved order of the result's columns; empty when there is none.</summary>
    public IReadOnlyList<string> ColumnOrder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ColumnOrderJson))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<string>>(ColumnOrderJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Seed values for the binds: those the query had when it was created or copied.</summary>
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
    public const int MaxViewRows = 1000;

    private const string PanelSelect = """
        SELECT p.id AS Id, p.watch_id AS WorkplaceId, p.name AS QueryName,
               p.sql_text AS Sql, p.last_params AS LastParamsJson,
               p.connection_name AS Connection, p.refresh_seconds AS RefreshSeconds,
               p.max_rows AS MaxRows, p.view_rows AS ViewRows, p.position AS Position,
               p.collapsed AS Collapsed, p.kind AS Kind,
               p.column_order AS ColumnOrderJson
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
    /// Copies panels of another workspace into this one, at the end, in the
    /// order they have there. Each copy is independent (its own query, connection,
    /// interval, row limits, seed values): changing it never touches the original.
    /// Only panels that belong to <paramref name="fromWorkplaceId"/> are copied;
    /// other ids are ignored, and importing from the workspace itself does
    /// nothing. Returns how many panels were added.
    /// </summary>
    public int ImportPanels(long workplaceId, long fromWorkplaceId, IEnumerable<long> panelIds)
    {
        var wanted = panelIds.Distinct().ToList();
        if (wanted.Count == 0 || fromWorkplaceId == workplaceId)
            return 0;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var sources = conn.Query<long>("""
            SELECT id FROM watch_panel
             WHERE watch_id = @fromWorkplaceId AND id IN @wanted
             ORDER BY position, id
            """, new { fromWorkplaceId, wanted }, tx).ToList();
        foreach (var source in sources)
            conn.Execute("""
                INSERT INTO watch_panel
                    (watch_id, name, sql_text, last_params, connection_name,
                     refresh_seconds, max_rows, view_rows, collapsed, kind, column_order, position)
                SELECT @workplaceId, p.name, p.sql_text, p.last_params, p.connection_name,
                       p.refresh_seconds, p.max_rows, p.view_rows, p.collapsed, p.kind, p.column_order,
                       (SELECT COALESCE(MAX(position), 0) + 1
                          FROM watch_panel WHERE watch_id = @workplaceId)
                  FROM watch_panel p WHERE p.id = @source
                """, new { workplaceId, source }, tx);
        tx.Commit();
        return sources.Count;
    }

    /// <summary>A panel with a query written in the workspace itself. Returns its id.</summary>
    public long AddNew(long workplaceId, string name, string sql, string connection)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>("""
            INSERT INTO watch_panel (watch_id, name, sql_text, connection_name, view_rows, position)
            VALUES (@workplaceId, @name, @sql, @connection, 0,
                    (SELECT COALESCE(MAX(position), 0) + 1
                       FROM watch_panel WHERE watch_id = @workplaceId));
            SELECT last_insert_rowid();
            """, new { workplaceId, name, sql, connection });
    }

    /// <summary>
    /// Adds the DBA templates with these keys to the workspace, at the end, in catalogue order
    /// (unknown keys are ignored), each as a panel on <paramref name="connection"/> with the
    /// template's refresh interval. Returns how many panels were added.
    /// </summary>
    public int AddTemplates(long workplaceId, string connection, IEnumerable<string> keys)
    {
        var wanted = keys.ToHashSet();
        var chosen = DbaTemplates.All.Where(t => wanted.Contains(t.Key)).ToList();
        if (chosen.Count == 0)
            return 0;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var t in chosen)
            conn.Execute("""
                INSERT INTO watch_panel
                    (watch_id, name, sql_text, connection_name, refresh_seconds, view_rows, kind, position)
                VALUES (@workplaceId, @name, @sql, @connection, @seconds, 0, @kind,
                        (SELECT COALESCE(MAX(position), 0) + 1
                           FROM watch_panel WHERE watch_id = @workplaceId))
                """, new
            {
                workplaceId, name = t.Title, sql = t.Sql.Trim().Replace("\r\n", "\n"), connection,
                seconds = t.RefreshSeconds, kind = t.Kind,
            }, tx);
        tx.Commit();
        return chosen.Count;
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
    /// connection, row limit (1..10,000) and visible rows (0..1,000, 0 = the
    /// default from Settings); a null
    /// connection or number leaves it as it is.
    /// </summary>
    public void SetPanel(
        long id, string? connection, long seconds, long? maxRows = null, long? viewRows = null)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE watch_panel
               SET connection_name = COALESCE(@connection, connection_name),
                   refresh_seconds = @seconds,
                   max_rows = COALESCE(@maxRows, max_rows),
                   view_rows = COALESCE(@viewRows, view_rows)
             WHERE id = @id
            """, new
        {
            id, connection,
            seconds = Math.Clamp(seconds, 0, MaxSeconds),
            maxRows = maxRows is { } m ? Math.Clamp(m, 1, MaxRowsLimit) : (long?)null,
            viewRows = viewRows is { } v ? Math.Clamp(v, 0, MaxViewRows) : (long?)null,
        });
    }

    /// <summary>Saves the order of the panel's columns (names); an empty list goes back to the query's own order.</summary>
    public void SetColumnOrder(long id, IReadOnlyList<string> names)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch_panel SET column_order = @json WHERE id = @id",
            new { id, json = names.Count == 0 ? "" : JsonSerializer.Serialize(names) });
    }

    /// <summary>Collapses a panel to its title line, or expands it.</summary>
    public void SetCollapsed(long id, bool collapsed)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch_panel SET collapsed = @collapsed WHERE id = @id",
            new { id, collapsed = collapsed ? 1 : 0 });
    }

    /// <summary>Collapses every panel of a workspace, or expands them all.</summary>
    public void SetAllCollapsed(long workplaceId, bool collapsed)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE watch_panel SET collapsed = @collapsed WHERE watch_id = @workplaceId",
            new { workplaceId, collapsed = collapsed ? 1 : 0 });
    }

    /// <summary>
    /// Puts the panels of a workspace in the given order (positions 1..n). The
    /// list must be exactly the workspace's panels, each once: anything else (a
    /// page that is out of date, a foreign id) changes nothing and returns false.
    /// </summary>
    public bool ReorderPanels(long workplaceId, IReadOnlyList<long> orderedIds)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var current = conn.Query<long>(
            "SELECT id FROM watch_panel WHERE watch_id = @workplaceId", new { workplaceId }, tx).ToHashSet();
        if (orderedIds.Count != current.Count || !current.SetEquals(orderedIds))
            return false;
        for (var i = 0; i < orderedIds.Count; i++)
            conn.Execute("UPDATE watch_panel SET position = @position WHERE id = @id",
                new { id = orderedIds[i], position = i + 1 }, tx);
        tx.Commit();
        return true;
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
                 refresh_seconds, max_rows, view_rows, collapsed, kind, column_order, position)
            SELECT @copy, name, sql_text, last_params, connection_name,
                   refresh_seconds, max_rows, view_rows, collapsed, kind, column_order, position
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
