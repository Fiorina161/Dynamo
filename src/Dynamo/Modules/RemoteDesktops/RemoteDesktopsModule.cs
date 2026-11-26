using Dynamo.Core;

namespace Dynamo.Modules.RemoteDesktops;

/// <summary>
/// Remote desktops (RDP) to open with one click: a name, a host, an account and its password. Connect saves the
/// password in the Windows Credential Manager of the person using this computer and starts the Remote Desktop client
/// in their session, so it connects without asking. It only works from the computer Dynamo runs on, since both steps
/// happen on that computer's desktop.
/// </summary>
public sealed class RemoteDesktopsModule : IModule
{
    public string Id => "rdp";
    public string Title => "Remote desktops";
    public string Href => "/RemoteDesktops";
    public int Order => 37;

    // A screen on a stand.
    public string Icon =>
        "<rect x=\"3\" y=\"4\" width=\"18\" height=\"12\" rx=\"1.5\"/><path d=\"M8 20h8M12 16v4\"/>";

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE remote_desktop (
            id       INTEGER PRIMARY KEY,
            name     TEXT NOT NULL COLLATE NOCASE UNIQUE,
            host     TEXT NOT NULL,
            username TEXT NOT NULL,
            password TEXT NOT NULL DEFAULT '');
        """,
    ];
}
