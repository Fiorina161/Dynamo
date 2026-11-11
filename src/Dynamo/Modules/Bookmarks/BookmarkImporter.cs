using System.Text.Json;

namespace Dynamo.Modules.Bookmarks;

/// <summary>
/// Imports the bookmarks.json of the former browser extension:
/// { "sections": [ { "name", "color", "links": [ { "name", "url" } ] } ] }.
/// Sections become groups (colors are dropped), file:/// links become plain
/// Windows paths. Idempotent: a bookmark whose group and target already
/// exist is skipped, so the import can be run again safely.
/// </summary>
public sealed class BookmarkImporter(BookmarkRepository repo)
{
    public sealed record Result(int Groups, int Added, int Skipped);

    public Result Import(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var board = repo.GetBoard();
        var groupIds = board.Groups.ToDictionary(
            g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        var known = board.Groups
            .SelectMany(g => g.Bookmarks.Select(b => (g.Id, b.Target)))
            .ToHashSet();

        int groups = 0, added = 0, skipped = 0;
        foreach (var section in doc.RootElement.GetProperty("sections").EnumerateArray())
        {
            var name = section.GetProperty("name").GetString()!.Trim();
            if (!groupIds.TryGetValue(name, out var groupId))
            {
                groupId = repo.SaveGroup(0, name);
                groupIds[name] = groupId;
                groups++;
            }

            foreach (var link in section.GetProperty("links").EnumerateArray())
            {
                var label = link.GetProperty("name").GetString()!.Trim();
                var target = TargetOf(link.GetProperty("url").GetString()!.Trim());
                if (!known.Add((groupId, target)))
                {
                    skipped++;
                    continue;
                }

                repo.SaveBookmark(0, label, target, groupId);
                added++;
            }
        }

        return new Result(groups, added, skipped);
    }

    /// <summary>file:///C:/a%20b/c.html becomes C:\a b\c.html.</summary>
    private static string TargetOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : url;
}
