using Dynamo.Core;

namespace Dynamo.Modules.Bookmarks;

public sealed class BookmarksModule : IModule
{
    public string Id => "bookmarks";
    public string Title => "Bookmarks";
    public string Href => "/Bookmarks";
    public int Order => 10;

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE bookmark_group (
            id        INTEGER PRIMARY KEY,
            name      TEXT    NOT NULL,
            position  INTEGER NOT NULL,
            collapsed INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE bookmark (
            id       INTEGER PRIMARY KEY,
            group_id INTEGER REFERENCES bookmark_group(id) ON DELETE SET NULL,
            label    TEXT    NOT NULL,
            target   TEXT    NOT NULL,
            position INTEGER NOT NULL);

        CREATE INDEX ix_bookmark_group ON bookmark(group_id, position);
        """,
    ];
}
