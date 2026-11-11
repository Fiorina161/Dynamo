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
    public string Title => "Oracle";
    public string Href => "/Oracle";
    public int Order => 20;

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
    ];
}
