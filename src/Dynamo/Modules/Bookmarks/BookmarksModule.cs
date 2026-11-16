using Dynamo.Core;

namespace Dynamo.Modules.Bookmarks;

public sealed class BookmarksModule : IModule
{
    public string Id => "bookmarks";
    public string Title => "Bookmarks";
    public string Href => "/Bookmarks";
    public int Order => 10;

    // A bookmark ribbon.
    public string Icon => "<path d=\"M6 3.5h12a1 1 0 0 1 1 1V21l-7-4.2L5 21V4.5a1 1 0 0 1 1-1z\"/>";

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
        // Optional tint of a group box: a key of GroupColors, null = none.
        "ALTER TABLE bookmark_group ADD COLUMN color TEXT;",
    ];
}
