using Dynamo.Core;

namespace Dynamo.Modules.Settings;

/// <summary>
/// Preferences and options, stored as key/value pairs in the same Sqlite
/// database as everything else. For now the only one is the password asked
/// from other computers (see <see cref="PasswordService"/>).
/// </summary>
public sealed class SettingsModule : IModule
{
    public string Id => "settings";
    public string Title => "Settings";
    public string Href => "/Settings";
    public int Order => 90;

    // Sliders.
    public string Icon =>
        "<path d=\"M4 7h9M17 7h3M4 17h3M11 17h9M4 12h13\"/>" +
        "<circle cx=\"15\" cy=\"7\" r=\"2\"/><circle cx=\"9\" cy=\"17\" r=\"2\"/><circle cx=\"19\" cy=\"12\" r=\"2\"/>";

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE setting (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """,
    ];
}
