namespace Dynamo.Modules.Settings;

/// <summary>
/// How query results are presented, as set in Settings (kept in the
/// <c>setting</c> table): how many highlight boxes show above the results, and
/// the default number of rows a panel shows (the height of its table).
/// </summary>
public sealed class ResultOptions(SettingsRepository settings)
{
    public const int MaxHighlightBoxes = 3;
    public const int DefaultHighlightBoxes = 1;
    private const string HighlightKey = "results.highlight_boxes";

    public const int MinDefaultRows = 1;
    public const int MaxDefaultRows = 1000;
    public const int FactoryDefaultRows = 10;
    private const string DefaultRowsKey = "results.default_rows";

    private readonly object _gate = new();
    private int? _highlightBoxes;
    private int? _defaultRows;

    /// <summary>
    /// How many rows a panel shows when its own "Visible rows" is 0 (1..1,000, default
    /// 10: what used to be hard-coded).
    /// </summary>
    public int DefaultRows
    {
        get
        {
            lock (_gate)
            {
                if (_defaultRows is null)
                {
                    _defaultRows = int.TryParse(settings.Get(DefaultRowsKey), out var saved)
                        ? Math.Clamp(saved, MinDefaultRows, MaxDefaultRows)
                        : FactoryDefaultRows;
                }
                return _defaultRows.Value;
            }
        }
    }

    public void SetDefaultRows(int rows)
    {
        rows = Math.Clamp(rows, MinDefaultRows, MaxDefaultRows);
        settings.Set(DefaultRowsKey, rows.ToString());
        lock (_gate) _defaultRows = rows;
    }

    /// <summary>0..3 highlight boxes above the results (default 1).</summary>
    public int HighlightBoxes
    {
        get
        {
            lock (_gate)
            {
                if (_highlightBoxes is null)
                {
                    _highlightBoxes = int.TryParse(settings.Get(HighlightKey), out var saved)
                        ? Math.Clamp(saved, 0, MaxHighlightBoxes)
                        : DefaultHighlightBoxes;
                }
                return _highlightBoxes.Value;
            }
        }
    }

    public void SetHighlightBoxes(int count)
    {
        count = Math.Clamp(count, 0, MaxHighlightBoxes);
        settings.Set(HighlightKey, count.ToString());
        lock (_gate) _highlightBoxes = count;
    }
}
