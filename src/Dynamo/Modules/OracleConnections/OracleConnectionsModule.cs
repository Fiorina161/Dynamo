using Dynamo.Core;

namespace Dynamo.Modules.OracleConnections;

/// <summary>
/// Named Oracle connections, in the spirit of tnsnames.ora: a name stands for
/// a data source plus the schema (user) and password to connect with. Other
/// features refer to a connection by its name only.
/// </summary>
public sealed class OracleConnectionsModule : IModule
{
    public string Id => "oracle";
    public string Title => "Connections";
    public string Href => "/Oracle";
    public int Order => 20;

    // A database: three stacked discs.
    public string Icon =>
        "<ellipse cx=\"12\" cy=\"5.5\" rx=\"8\" ry=\"3\"/>" +
        "<path d=\"M4 5.5v13c0 1.7 3.6 3 8 3s8-1.3 8-3v-13\"/>" +
        "<path d=\"M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3\"/>";

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE oracle_connection (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL COLLATE NOCASE UNIQUE,
            data_source TEXT NOT NULL,
            username    TEXT NOT NULL,
            password    TEXT NOT NULL DEFAULT '');
        """,
        // A pinned connection is listed (and offered in pickers) first.
        "ALTER TABLE oracle_connection ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;",
        // A DBA connection logs in AS SYSDBA; the DBA features (templates, the
        // sessions panel) are offered only on those.
        "ALTER TABLE oracle_connection ADD COLUMN dba INTEGER NOT NULL DEFAULT 0;",
        // One of the pastel colours of ConnectionColors, or NULL: the title bar of
        // the panels that use the connection is drawn in it.
        "ALTER TABLE oracle_connection ADD COLUMN color TEXT;",
    ];
}
