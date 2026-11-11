namespace Dynamo.Modules.OracleConnections;

/// <summary>
/// Creates a connection for each alias of a tnsnames.ora: the alias becomes
/// the name and its descriptor the data source. The schema and the password
/// are left empty, to be filled in on the Oracle page.
/// </summary>
public sealed class TnsNamesImporter(OracleConnectionRepository repo)
{
    public sealed record Result(
        int Added,
        int AlreadyThere,
        IReadOnlyList<string> Identical,
        IReadOnlyList<string> Conflicting);

    /// <summary>
    /// Idempotent: a name already defined (any case) is left alone, so what was
    /// filled in survives a second import. An alias defined several times in the
    /// file keeps its first definition; the names whose later definitions differ
    /// are reported, since Oracle would have used only one of them.
    /// </summary>
    public Result Import(string file)
    {
        var entries = TnsNamesParser.Parse(File.ReadAllText(file));
        var known = repo.GetAll().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, TnsEntry>(StringComparer.OrdinalIgnoreCase);
        var identical = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicting = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, alreadyThere = 0;

        foreach (var entry in entries)
        {
            if (firstSeen.TryGetValue(entry.Alias, out var first))
            {
                // Reported under the spelling that was kept.
                if (first.Descriptor == entry.Descriptor)
                    identical.Add(first.Alias);
                else
                    conflicting.Add(first.Alias);
                continue;
            }

            firstSeen[entry.Alias] = entry;
            if (!known.Add(entry.Alias))
            {
                alreadyThere++;
                continue;
            }

            repo.Save(new OracleConnectionInfo { Name = entry.Alias, DataSource = entry.Descriptor });
            added++;
        }

        return new Result(added, alreadyThere, identical.ToList(), conflicting.ToList());
    }
}
