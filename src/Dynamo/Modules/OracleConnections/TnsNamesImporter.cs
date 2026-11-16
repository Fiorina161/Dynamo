namespace Dynamo.Modules.OracleConnections;

/// <summary>Where the user's tnsnames.ora is, when there is one.</summary>
public static class TnsNamesLocator
{
    /// <summary>
    /// %TNS_ADMIN%\tnsnames.ora, else %ORACLE_HOME%\network\admin\tnsnames.ora;
    /// null when neither exists. (A service sees machine-wide variables only.)
    /// </summary>
    public static string? Find()
    {
        var tnsAdmin = Environment.GetEnvironmentVariable("TNS_ADMIN");
        if (tnsAdmin is { Length: > 0 }
            && Path.Combine(tnsAdmin, "tnsnames.ora") is var a && File.Exists(a))
            return a;

        var oracleHome = Environment.GetEnvironmentVariable("ORACLE_HOME");
        if (oracleHome is { Length: > 0 }
            && Path.Combine(oracleHome, "network", "admin", "tnsnames.ora") is var b && File.Exists(b))
            return b;

        return null;
    }
}

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

    /// <summary>An alias that could be imported: not defined yet as a connection.</summary>
    /// <param name="Definitions">How many times the file defines it.</param>
    /// <param name="Conflicting">A later definition differs from the first.</param>
    public sealed record Candidate(
        string Alias, string Descriptor, int Definitions, bool Conflicting);

    /// <summary>What a file would add: the new aliases, and how many exist already.</summary>
    public sealed record Preview(IReadOnlyList<Candidate> New, int AlreadyThere);

    /// <summary>
    /// Reads the file without changing anything: each alias once (first
    /// definition), in file order, split into new ones and ones whose name is
    /// already a connection (any case).
    /// </summary>
    public Preview Inspect(string file)
    {
        var entries = TnsNamesParser.Parse(File.ReadAllText(file));
        var known = repo.GetAll().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var groups = new Dictionary<string, List<TnsEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!groups.TryGetValue(entry.Alias, out var list))
            {
                groups[entry.Alias] = list = [];
                order.Add(entry.Alias);
            }
            list.Add(entry);
        }

        var added = new List<Candidate>();
        var already = 0;
        foreach (var alias in order)
        {
            var list = groups[alias];
            if (known.Contains(alias))
            {
                already++;
                continue;
            }
            added.Add(new Candidate(
                list[0].Alias, list[0].Descriptor, list.Count,
                list.Any(e => e.Descriptor != list[0].Descriptor)));
        }
        return new Preview(added, already);
    }

    /// <summary>
    /// Idempotent: a name already defined (any case) is left alone, so what was
    /// filled in survives a second import. An alias defined several times in the
    /// file keeps its first definition; the names whose later definitions differ
    /// are reported, since Oracle would have used only one of them.
    /// </summary>
    /// <param name="only">
    /// Import just these aliases (any case); null imports every alias of the file.
    /// </param>
    public Result Import(string file, IEnumerable<string>? only = null)
    {
        var entries = TnsNamesParser.Parse(File.ReadAllText(file));
        if (only is not null)
        {
            var wanted = only.ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries = entries.Where(e => wanted.Contains(e.Alias)).ToList();
        }

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
