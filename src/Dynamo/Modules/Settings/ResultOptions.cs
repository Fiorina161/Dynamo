namespace Dynamo.Modules.Settings;

/// <summary>
/// How query results are presented, as set in Settings (kept in the
/// <c>setting</c> table). For now: how many highlight boxes show above the
/// results.
/// </summary>
public sealed class ResultOptions(SettingsRepository settings)
{
    public const int MaxHighlightBoxes = 3;
    public const int DefaultHighlightBoxes = 1;
    private const string HighlightKey = "results.highlight_boxes";

    private readonly object _gate = new();
    private int? _highlightBoxes;

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
