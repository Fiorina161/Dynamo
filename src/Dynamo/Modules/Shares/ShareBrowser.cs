using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dynamo.Modules.Shares;

/// <summary>
/// How names are compared when filtering: case and accents do not matter, so <c>é</c>, <c>è</c>, <c>E</c> and <c>e</c>
/// all match <c>e</c>. Each text is decomposed (<c>é</c> becomes <c>e</c> and an accent mark), the marks are dropped and
/// the case is folded; it does not depend on the computer's culture. Letters that do not decompose (<c>œ</c>, <c>ø</c>)
/// stay as they are.
/// </summary>
public static class TextMatch
{
    /// <summary>The text without accents, in lower case.</summary>
    public static string Fold(string text)
    {
        if (text.Length == 0)
            return text;
        string decomposed;
        try
        {
            decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            decomposed = text;                                  // a broken surrogate in a file name: compare it as it is
        }
        var folded = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(c));
        }
        return folded.ToString();
    }

    /// <summary>True if <paramref name="foldedNeedle"/> (already folded) is in the text, ignoring case and accents.</summary>
    public static bool Contains(string text, string foldedNeedle) =>
        Fold(text).Contains(foldedNeedle, StringComparison.Ordinal);
}

/// <summary>A refusal or failure the user can act on (wrong password, folder not found, no access...).</summary>
public sealed class ShareException(string message) : Exception(message);

/// <summary>
/// What a network folder path may be, and how a path inside a folder is made safe. A folder is always a network
/// path, <c>\\server\share</c> or deeper: nothing local, so that Dynamo (which can run as LocalSystem, able to read
/// nearly everything on its own computer) can only ever read what the account saved with the folder can read over
/// the network.
/// </summary>
public static class SharePath
{
    private static readonly char[] Forbidden = ['"', '<', '>', '|', '?', '*', ':'];

    /// <summary>The normalised path (<c>\\server\share\folder</c>), or why it is not acceptable.</summary>
    public static (string? Path, string? Error) Parse(string? input)
    {
        var text = (input ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
        if (text.Length == 0)
            return (null, "The folder is required.");
        if (text.Length > 500)
            return (null, "The folder is too long (max 500).");
        if (!text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith(@"\\\", StringComparison.Ordinal))
            return (null, @"Give a network folder: \\server\share or \\server\share\folder.");
        if (text.Any(char.IsControl) || text.IndexOfAny(Forbidden) >= 0)
            return (null, @"The folder has a character that a Windows path cannot have (: * ? "" < > |).");
        var parts = text[2..].Split('\\');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0))
            return (null, @"Give a network folder: \\server\share or \\server\share\folder.");
        if (parts[0] is "." or "?" || parts.Any(p => p.Trim('.', ' ').Length == 0))
            return (null, "That is not a network folder.");
        return (@"\\" + string.Join('\\', parts), null);
    }

    /// <summary>The <c>\\server\share</c> part, which is what a login is made to.</summary>
    public static string ShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\');
        return parts.Length < 2 ? unc : @"\\" + parts[0] + "\\" + parts[1];
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> inside <paramref name="root"/>, or null when it is not inside
    /// (a <c>..</c>, a rooted path, a drive, a stream name, a character no file name has). The relative path comes
    /// from the address bar: nothing in it is trusted.
    /// </summary>
    public static string? Resolve(string root, string? relative, out string clean)
    {
        clean = "";
        var segments = (relative ?? "").Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var s in segments)
        {
            if (s.Trim('.', ' ').Length == 0 || s.IndexOfAny(invalid) >= 0 || s.Length > 255)
                return null;
        }
        var baseDir = root.TrimEnd('\\');
        var full = segments.Length == 0 ? baseDir : Path.GetFullPath(Path.Combine(baseDir, string.Join('\\', segments)));
        if (segments.Length > 0 && !full.StartsWith(baseDir + "\\", StringComparison.OrdinalIgnoreCase))
            return null;
        clean = string.Join('\\', segments);
        return full;
    }
}

/// <summary>Logs a process in to a network share with an account (an interface so tests need no network).</summary>
public interface IShareConnector
{
    /// <summary>Null when connected, else why not, in words.</summary>
    string? Connect(string shareRoot, string username, string password);
}

/// <summary>
/// WNetAddConnection2: connects the process to <c>\\server\share</c> with an account, without a drive letter, so the
/// ordinary file functions then read the share as that account. This is how a Windows service (LocalSystem, which has
/// no network account of its own) can read a share. Windows allows one account per server for a process: when it
/// complains of a conflict, the connections this class made to that server are dropped and it tries again.
/// </summary>
public sealed class ShareConnector : IShareConnector
{
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ResourceTypeDisk = 1;

    private static readonly object Gate = new();
    private static readonly HashSet<string> Ours = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    private static int Add(string root, string username, string password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = root };
        return WNetAddConnection2(ref resource, password, username, 0);
    }

    /// <summary>What a Windows error code means, in words for the user.</summary>
    public static string Describe(int code) => code switch
    {
        ErrorSessionCredentialConflict =>
            "Windows already has a connection to that server with another account: close it (net use \\\\server /delete) and try again.",
        _ => new Win32Exception(code).Message.Trim(),
    };

    public string? Connect(string shareRoot, string username, string password)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        lock (Gate)
        {
            var code = Add(shareRoot, username, password);
            if (code == ErrorSessionCredentialConflict)
            {
                var server = shareRoot.TrimStart('\\').Split('\\')[0];
                foreach (var other in Ours.Where(o => o.TrimStart('\\').Split('\\')[0].Equals(server, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    WNetCancelConnection2(other, 0, true);
                    Ours.Remove(other);
                }
                code = Add(shareRoot, username, password);
            }
            if (code != 0)
                return Describe(code);
            Ours.Add(shareRoot);
            return null;
        }
    }
}

/// <summary>One file or folder of a listing.</summary>
public sealed record ShareEntry(string Name, bool IsDirectory, long Size, DateTime Modified, string Relative);

/// <summary>One page of the files and folders of a folder, with where it is.</summary>
public sealed record ShareListing(
    string Relative, IReadOnlyList<ShareEntry> Entries, int Total, int Page, int PageSize, bool Capped,
    IReadOnlyList<(string Name, string Relative)> Breadcrumb)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>A file opened to be sent to the browser.</summary>
public sealed record ShareDownload(Stream Stream, string Name, long Length);

/// <summary>Lists and downloads from a shared folder; an interface so the pages can be tested without a network.</summary>
public interface IShareBrowser
{
    /// <param name="anyOf">
    /// The ticked substrings: when there are some, a file is listed only if its name contains one of them (any,
    /// case and accents ignored); folders are always listed, so the tree can still be walked. Empty or null: no such filter.
    /// </param>
    Task<ShareListing> ListAsync(
        SharedFolder folder, string? relative, string? filter, string? sort, bool descending, int page,
        CancellationToken ct = default, IReadOnlyList<string>? anyOf = null);

    Task<ShareDownload> OpenAsync(SharedFolder folder, string? relative, CancellationToken ct);

    /// <summary>Connects and reads the top of the folder; the message says what was found. Throws <see cref="ShareException"/>.</summary>
    Task<string> TestAsync(SharedFolder folder, CancellationToken ct);
}

/// <summary>
/// Reads a network folder with the account saved with it. Read only: it lists, and opens a file for reading; it never
/// writes, deletes or renames anything. The address bar's relative path is resolved with <see cref="SharePath.Resolve"/>,
/// so nothing outside the folder can be reached.
/// </summary>
public sealed class ShareBrowser(IShareConnector connector) : IShareBrowser
{
    public const int PageSize = 200;

    /// <summary>More entries than this in one folder are not listed (the page says so and the filter narrows it).</summary>
    public const int MaxEntries = 50_000;

    private void Connect(SharedFolder folder)
    {
        if (folder.PasswordUnreadable)
            throw new ShareException(
                "The saved password cannot be read on this computer (the database came from another one, or Windows was reinstalled): edit the folder and enter it again.");
        if (!folder.Path.StartsWith(@"\\", StringComparison.Ordinal))
            return;                                     // not a network path: nothing to log in to (only tests get here)
        if (folder.Username.Length == 0)
            throw new ShareException("No account is saved with this folder: edit it and enter one.");
        if (connector.Connect(SharePath.ShareRoot(folder.Path), folder.Username, folder.Password) is { } error)
            throw new ShareException($"Could not connect to {SharePath.ShareRoot(folder.Path)}: {error}");
    }

    private static string FolderOf(SharedFolder folder, string? relative, out string clean) =>
        SharePath.Resolve(folder.Path, relative, out clean) ?? throw new ShareException("That path is not inside the folder.");

    private static ShareException Explain(Exception ex, string full) => ex switch
    {
        UnauthorizedAccessException => new ShareException("Access denied: the saved account is not allowed to read that."),
        DirectoryNotFoundException or FileNotFoundException => new ShareException("That does not exist (or is not shared): " + full),
        IOException io => new ShareException(io.Message.Split('\n', 2)[0].Trim()),
        _ => new ShareException(ex.Message.Split('\n', 2)[0].Trim()),
    };

    public Task<ShareListing> ListAsync(
        SharedFolder folder, string? relative, string? filter, string? sort, bool descending, int page,
        CancellationToken ct = default, IReadOnlyList<string>? anyOf = null) =>
        Task.Run(() =>
        {
            Connect(folder);
            var full = FolderOf(folder, relative, out var clean);
            var entries = new List<ShareEntry>();
            var capped = false;
            var substrings = (anyOf ?? []).Where(s => s.Length > 0).Select(TextMatch.Fold).ToList();
            var wantedFolded = TextMatch.Fold((filter ?? "").Trim());
            try
            {
                var wanted = (filter ?? "").Trim();
                foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (wanted.Length > 0 && !TextMatch.Contains(info.Name, wantedFolded))
                        continue;
                    if (substrings.Count > 0 && info is not DirectoryInfo)
                    {
                        var name = TextMatch.Fold(info.Name);
                        if (!substrings.Any(s => name.Contains(s, StringComparison.Ordinal)))
                            continue;
                    }
                    if (entries.Count >= MaxEntries)
                    {
                        capped = true;
                        break;
                    }
                    var isDirectory = info is DirectoryInfo;
                    entries.Add(new ShareEntry(
                        info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTime,
                        clean.Length == 0 ? info.Name : clean + "\\" + info.Name));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ShareException)
            {
                throw Explain(ex, full);
            }

            // Folders first, then the order asked for; the name decides a tie.
            IOrderedEnumerable<ShareEntry> ordered = entries.OrderByDescending(e => e.IsDirectory);
            var byName = StringComparer.OrdinalIgnoreCase;
            ordered = (sort, descending) switch
            {
                ("size", false) => ordered.ThenBy(e => e.Size).ThenBy(e => e.Name, byName),
                ("size", true) => ordered.ThenByDescending(e => e.Size).ThenBy(e => e.Name, byName),
                ("date", false) => ordered.ThenBy(e => e.Modified).ThenBy(e => e.Name, byName),
                ("date", true) => ordered.ThenByDescending(e => e.Modified).ThenBy(e => e.Name, byName),
                (_, true) => ordered.ThenByDescending(e => e.Name, byName),
                _ => ordered.ThenBy(e => e.Name, byName),
            };
            var sorted = ordered.ToList();
            var pages = Math.Max(1, (sorted.Count + PageSize - 1) / PageSize);
            var current = Math.Clamp(page, 1, pages);
            var crumbs = new List<(string, string)>();
            var soFar = "";
            foreach (var segment in clean.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                soFar = soFar.Length == 0 ? segment : soFar + "\\" + segment;
                crumbs.Add((segment, soFar));
            }
            return new ShareListing(
                clean, sorted.Skip((current - 1) * PageSize).Take(PageSize).ToList(), sorted.Count, current, PageSize, capped, crumbs);
        }, ct);

    public Task<ShareDownload> OpenAsync(SharedFolder folder, string? relative, CancellationToken ct) =>
        Task.Run(() =>
        {
            Connect(folder);
            var full = FolderOf(folder, relative, out _);
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists)
                    throw new FileNotFoundException(full);
                // Read only, and sharing with whoever writes it: a trace file can still be open on its server.
                var stream = new FileStream(
                    full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                return new ShareDownload(stream, info.Name, info.Length);
            }
            catch (Exception ex) when (ex is not ShareException)
            {
                throw Explain(ex, full);
            }
        }, ct);

    public async Task<string> TestAsync(SharedFolder folder, CancellationToken ct)
    {
        var listing = await ListAsync(folder, null, null, null, false, 1, ct);
        var folders = listing.Entries.Count(e => e.IsDirectory);
        return $"Connected to “{folder.Name}”: {listing.Total} {(listing.Total == 1 ? "entry" : "entries")}" +
               (listing.Total > 0 ? $" ({folders} {(folders == 1 ? "folder" : "folders")})" : "") + ".";
    }
}
