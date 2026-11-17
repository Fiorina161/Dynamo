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

/// <summary>A release published on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version, string Tag, string Name, string PageUrl, string? DownloadUrl, DateTimeOffset? PublishedAt);

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

    private const string EnabledKey = "updates.check";

    private readonly IHttpClientFactory _http;
    private readonly SettingsRepository _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly Version _current;
    private readonly object _gate = new();
    private UpdateStatus _status = new(null, null, null);
    private bool? _enabled;

    /// <param name="now">The clock (tests move it).</param>
    /// <param name="current">The running version (tests choose it).</param>
    public UpdateChecker(
        IHttpClientFactory http, SettingsRepository settings,
        Func<DateTimeOffset>? now = null, Version? current = null)
    {
        _http = http;
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _current = current ?? AppVersion.Current;
    }

    public Version Current => _current;

    /// <summary>Look once a day on its own (default yes). Off: only "Check now" asks.</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                _enabled ??= _settings.Get(EnabledKey) != "0";
                return _enabled.Value;
            }
        }
        set
        {
            _settings.Set(EnabledKey, value ? "1" : "0");
            lock (_gate) _enabled = value;
        }
    }

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>A newer release than the running one is published.</summary>
    public bool Available => Status.Latest is { } latest && latest.Version > _current;

    /// <summary>The automatic look: does nothing when the user turned it off.</summary>
    public async Task CheckIfEnabledAsync(CancellationToken ct = default)
    {
        if (Enabled)
            await CheckAsync(ct);
    }

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

        lock (_gate) _status = status;
        return status;
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
            string? download = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    if (assetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true
                        && Trusted(asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null) is { } url)
                    {
                        download = url;
                        break;
                    }
                }

            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var when)
                ? when : null;
            return Result(new UpdateInfo(version, tag, name, page, download, published), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Result(null, "GitHub's answer could not be read.");
        }
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

/// <summary>Looks for a new version a little after the start, then once a day.</summary>
public sealed class UpdateCheckService(UpdateChecker checker, ILogger<UpdateCheckService> log) : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Every);
            do
            {
                await checker.CheckIfEnabledAsync(stoppingToken);
                if (checker.Available)
                    log.LogInformation("A new Dynamo version is available: {Version}", checker.Status.Latest!.Tag);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
