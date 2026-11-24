using System.Reflection;
using System.Text.Json;

namespace Dynamo.Modules.Settings;

/// <summary>The version of this build (set in the project file).</summary>
public static class AppVersion
{
    public static Version Current { get; } = Read();

    private static Version Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        // "1.2.3+abcdef" (the build adds the commit): keep the part before the +.
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        return Version.TryParse(text, out var version) ? version : assembly.GetName().Version ?? new Version(0, 0);
    }

    /// <summary>"1.2.3", whatever number of parts the version has.</summary>
    public static string Format(Version version) =>
        version.Revision > 0 ? version.ToString()
        : version.Build >= 0 ? version.ToString(3)
        : version.ToString(2);
}

/// <summary>What one version changed: the lines of its entry in the changelog.</summary>
public sealed record ChangeEntry(Version Version, string? Date, IReadOnlyList<string> Items);

/// <summary>
/// A release published on GitHub. <see cref="Changes"/> are the changes since the
/// running version up to this release, newest first (empty when unknown).
/// </summary>
public sealed record UpdateInfo(
    Version Version, string Tag, string Name, string PageUrl, string? DownloadUrl, DateTimeOffset? PublishedAt,
    string? Sha256 = null, IReadOnlyList<ChangeEntry>? Changes = null);

/// <summary>What the last look at GitHub found: a release, or why not.</summary>
public sealed record UpdateStatus(DateTimeOffset? CheckedAt, UpdateInfo? Latest, string? Error);

/// <summary>
/// Asks GitHub, from time to time, whether a newer release exists, so the user
/// can be told and download it. It only informs: nothing is downloaded or
/// installed. The repository is public, so the question needs no token; it
/// sends nothing about this computer or its user.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "Fiorina161/Dynamo";
    public const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string RepositoryUrl = $"https://github.com/{Repository}";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    public const string ClientName = "github";

    /// <summary>
    /// The project's changelog (one entry per release, written by the release script),
    /// read to tell what a new version changed. A fixed address on GitHub's raw file
    /// host for this repository; nothing from an answer ever chooses it.
    /// </summary>
    public const string ChangelogUrl = $"https://raw.githubusercontent.com/{Repository}/master/CHANGELOG.md";
    private const int MaxChangelogBytes = 1_000_000;
    private const int MaxEntries = 40;
    private const int MaxItemsPerEntry = 60;
    private const int MaxItemLength = 400;

    private readonly IHttpClientFactory _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly Version _current;
    private readonly object _gate = new();
    private UpdateStatus _status = new(null, null, null);

    /// <param name="now">The clock (tests move it).</param>
    /// <param name="current">The running version (tests choose it).</param>
    public UpdateChecker(
        IHttpClientFactory http,
        Func<DateTimeOffset>? now = null, Version? current = null)
    {
        _http = http;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _current = current ?? AppVersion.Current;
    }

    public Version Current => _current;

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>A newer release than the running one is published.</summary>
    public bool Available => Status.Latest is { } latest && latest.Version > _current;

    /// <summary>Asks GitHub for the latest release. Never throws: a failure becomes a message.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        UpdateStatus status;
        try
        {
            using var client = _http.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, ct);

            status = response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => Result(null, "No release has been published yet."),
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests =>
                    Result(null, "GitHub refused the request (probably a rate limit). Try again later."),
                _ when !response.IsSuccessStatusCode => Result(null, $"GitHub answered {(int)response.StatusCode}."),
                _ => Parse(await response.Content.ReadAsStringAsync(ct)),
            };
        }
        catch (HttpRequestException ex)
        {
            status = Result(null, $"Could not reach GitHub: {ex.Message}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            status = Result(null, "GitHub did not answer in time.");
        }

        // A newer release: say what it changes since the running version (best effort).
        if (status.Latest is { } latest && latest.Version > _current)
        {
            var changes = await LoadChangesAsync(latest.Version, ct);
            if (changes.Count > 0)
                status = status with { Latest = latest with { Changes = changes } };
        }

        lock (_gate) _status = status;
        return status;
    }

    /// <summary>
    /// The changelog entries after the running version, up to <paramref name="latest"/>.
    /// Never throws: when the file cannot be read or has nothing usable, the release's
    /// own notes (already in the status) are what is shown.
    /// </summary>
    private async Task<IReadOnlyList<ChangeEntry>> LoadChangesAsync(Version latest, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, ChangelogUrl);
            request.Headers.TryAddWithoutValidation("Accept", "text/plain");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxChangelogBytes)
                return [];
            return ParseChangelog(await response.Content.ReadAsStringAsync(ct), _current, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// The entries of a changelog ("## 1.2.3 - 2026-11-24", then "- a change" lines)
    /// that are newer than <paramref name="after"/> and not newer than
    /// <paramref name="upTo"/>, newest first. Anything else in the file is ignored, and
    /// the amount kept is limited: the text only ever comes from one public file.
    /// </summary>
    public static IReadOnlyList<ChangeEntry> ParseChangelog(string text, Version after, Version upTo)
    {
        var entries = new List<ChangeEntry>();
        Version? version = null;
        string? date = null;
        List<string>? items = null;

        void Close()
        {
            if (version is not null && items is { Count: > 0 } && version > after && version <= upTo)
                entries.Add(new ChangeEntry(version, date, items));
            version = null;
            items = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                var header = line[3..].Trim().Split(" - ", 2);
                if (TryParseVersion(header[0], out var parsed))
                {
                    version = parsed;
                    date = header.Length > 1 && header[1].Length <= 20 ? header[1].Trim() : null;
                    items = [];
                }
            }
            else if (items is not null && line.StartsWith("- ", StringComparison.Ordinal) && items.Count < MaxItemsPerEntry)
            {
                var item = new string(line[2..].Trim().Where(c => !char.IsControl(c)).ToArray());
                if (item.Length > 0)
                    items.Add(item.Length > MaxItemLength ? item[..MaxItemLength] + "…" : item);
            }
        }
        Close();

        return entries.OrderByDescending(e => e.Version).Take(MaxEntries).ToList();
    }

    /// <summary>
    /// The change lines of a release's own notes ("## Dynamo 1.2.3", then "- a change"
    /// lines, up to the next heading): what is shown when the changelog is out of reach.
    /// </summary>
    public static IReadOnlyList<string> ItemsInNotes(string? notes)
    {
        var items = new List<string>();
        foreach (var raw in (notes ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("### ", StringComparison.Ordinal))
                break;
            if (line.StartsWith("- ", StringComparison.Ordinal) && items.Count < MaxItemsPerEntry)
            {
                var item = new string(line[2..].Trim().Where(c => !char.IsControl(c)).ToArray());
                if (item.Length > 0)
                    items.Add(item.Length > MaxItemLength ? item[..MaxItemLength] + "…" : item);
            }
        }
        return items;
    }

    private UpdateStatus Result(UpdateInfo? latest, string? error) => new(_now(), latest, error);

    private UpdateStatus Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version))
                return Result(null, $"The latest release has an unexpected version name (\"{tag}\").");

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : tag;
            var page = Trusted(root.TryGetProperty("html_url", out var h) ? h.GetString() : null) ?? ReleasesPage;
            string? download = null, sha256 = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    if (assetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true
                        && Trusted(asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null) is { } url)
                    {
                        download = url;
                        // The checksum GitHub itself computed for the file ("sha256:..."), else
                        // the one the release notes give for this file.
                        var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() : null;
                        sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? digest[7..]
                            : ChecksumInNotes(root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                                ? b.GetString() : null, assetName);
                        break;
                    }
                }

            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var when)
                ? when : null;
            var notes = ItemsInNotes(root.TryGetProperty("body", out var nb) && nb.ValueKind == JsonValueKind.String
                ? nb.GetString() : null);
            // (replaced by the changelog's entries since the running version when they can be read)
            IReadOnlyList<ChangeEntry> own = notes.Count > 0
                ? [new ChangeEntry(version, published?.ToString("yyyy-MM-dd"), notes)] : [];
            return Result(new UpdateInfo(version, tag, name, page, download, published, sha256, own), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Result(null, "GitHub's answer could not be read.");
        }
    }

    /// <summary>
    /// The SHA-256 the release notes give for a file ("`hash`  `Dynamo-1.2.3.zip`",
    /// as release.ps1 writes it); null when there is none.
    /// </summary>
    public static string? ChecksumInNotes(string? notes, string fileName)
    {
        if (string.IsNullOrEmpty(notes))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            notes, @"(?<hash>(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F]))[`'\s]*" + System.Text.RegularExpressions.Regex.Escape(fileName));
        return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    /// <summary>The release's version as text, for the pages ("1.2.3").</summary>
    public static string ToText(UpdateInfo release) => AppVersion.Format(release.Version);

    /// <summary>"v1.2.3", "1.2.3" or "1.2" as a version; false for anything else.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        if (!text.Contains('.'))
            text += ".0";
        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// Only a link into this project's GitHub pages is ever shown: whatever
    /// the answer says, the user is never sent to another site from here.
    /// </summary>
    public static string? Trusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath.Equals($"/{Repository}", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith($"/{Repository}/", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;
}
