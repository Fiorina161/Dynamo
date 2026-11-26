using Dynamo.Core;

namespace Dynamo.Modules.Shares;

/// <summary>
/// Network folders (a shared folder of another computer, <c>\\server\share\folder</c>) that can be listed and
/// downloaded from, each with the account used to read it. The page of a folder shows its files and sub-folders;
/// a file is downloaded through the browser.
/// </summary>
public sealed class SharesModule : IModule
{
    public string Id => "shares";
    public string Title => "Shared folders";
    public string Href => "/Shares";
    public int Order => 36;

    // A folder.
    public string Icon =>
        "<path d=\"M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v8.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z\"/>";

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE shared_folder (
            id       INTEGER PRIMARY KEY,
            name     TEXT NOT NULL COLLATE NOCASE UNIQUE,
            path     TEXT NOT NULL,
            username TEXT NOT NULL DEFAULT '',
            password TEXT NOT NULL DEFAULT '');
        """,
        // The substrings of a folder's filter list: the ticked ones (active) narrow the file list.
        """
        CREATE TABLE shared_folder_filter (
            id        INTEGER PRIMARY KEY,
            folder_id INTEGER NOT NULL REFERENCES shared_folder(id) ON DELETE CASCADE,
            text      TEXT NOT NULL,
            active    INTEGER NOT NULL DEFAULT 0,
            position  INTEGER NOT NULL);
        CREATE INDEX ix_shared_folder_filter ON shared_folder_filter(folder_id, position);
        """,
        // Whether files may be uploaded to the folder (1 = yes). The folders that existed when uploads were added are on.
        "ALTER TABLE shared_folder ADD COLUMN allow_upload INTEGER NOT NULL DEFAULT 1;",
    ];
}
