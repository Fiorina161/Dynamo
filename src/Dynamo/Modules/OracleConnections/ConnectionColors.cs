namespace Dynamo.Modules.OracleConnections;

/// <summary>
/// The six pastel colours a connection can be given. The title bar of every panel
/// that runs on the connection is drawn in it, so a production database cannot be
/// mistaken for a test one. Only the key is stored; the shades (light and dark theme)
/// are the <c>--conn-*</c> variables of site.css.
/// </summary>
public static class ConnectionColors
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("blue", "Blue"),
        ("teal", "Teal"),
        ("green", "Green"),
        ("sand", "Sand"),
        ("rose", "Rose"),
        ("violet", "Violet"),
    ];

    /// <summary>The key when it is one of the six, otherwise null.</summary>
    public static string? Normalize(string? key) =>
        All.Any(c => c.Key == key) ? key : null;
}
