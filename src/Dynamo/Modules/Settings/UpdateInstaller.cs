using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dynamo.Core;

namespace Dynamo.Modules.Settings;

/// <summary>What the "Update now" button got done.</summary>
public sealed record UpdateOutcome(bool Started, string? Error, string? Version = null);

/// <summary>Where Dynamo runs from, set at start-up (tests change it).</summary>
public sealed record UpdateInstallerOptions(bool RunsAsService, int HttpPort);

/// <summary>Starts the script that swaps the program's files (a seam for the tests).</summary>
public interface IUpdateLauncher
{
    void Start(string scriptPath, IReadOnlyList<string> arguments);
}

/// <summary>Runs the script with Windows PowerShell, detached from the service.</summary>
public sealed class PowerShellUpdateLauncher : IUpdateLauncher
{
    public void Start(string scriptPath, IReadOnlyList<string> arguments)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            info.ArgumentList.Add(a);
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        Process.Start(info)?.Dispose();
    }
}

/// <summary>
/// "Update now": downloads the latest release from this project's GitHub page,
/// checks it, and hands the swap of the program's files to a script that
/// stops the service, replaces the files, starts it again and goes back to
/// the old files if the new version does not come up.
///
/// The service runs as LocalSystem, so what it installs runs with full rights.
/// Hence the limits: it only ever fetches the release asset of this project
/// (an exact https address on github.com), only a version newer than the
/// running one, and only when the release publishes a SHA-256 that the
/// downloaded file matches and the program inside reports the same version.
/// The checksum comes through the same GitHub account as the file, so it
/// protects against a corrupted or altered download, not against a stolen
/// account: the releases are not code-signed.
/// </summary>
public sealed partial class UpdateInstaller(
    IHttpClientFactory http, UpdateChecker checker, DynamoHome home,
    IUpdateLauncher launcher, UpdateInstallerOptions options, ILogger<UpdateInstaller> log,
    Func<string, Version?>? readVersion = null)
{
    public const string DownloadClientName = "github-download";
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    // The one kind of address it will download from.
    [GeneratedRegex(@"^https://github\.com/Fiorina161/Dynamo/releases/download/[^/]+/Dynamo-[0-9][0-9.]*\.zip$")]
    private static partial Regex AssetAddress();

    private readonly Func<string, Version?> _readVersion = readVersion ?? ProductVersionOf;
    private int _running;

    /// <summary>Only a service can replace itself; a console run cannot.</summary>
    public bool Supported => options.RunsAsService;

    public string UpdateFolder => Path.Combine(home.Path, "update");
    public string LogFile => Path.Combine(home.Path, "update.log");
    private string ResultFile => Path.Combine(UpdateFolder, "last-result.txt");

    /// <summary>
    /// Why the last update did not happen (the script went back to the old
    /// version), or null.
    /// </summary>
    public string? LastFailure
    {
        get
        {
            try
            {
                var text = File.Exists(ResultFile) ? File.ReadAllText(ResultFile).Trim() : "";
                return text.StartsWith("failed", StringComparison.OrdinalIgnoreCase) ? text : null;
            }
            catch (IOException) { return null; }
        }
    }

    public async Task<UpdateOutcome> InstallAsync(CancellationToken ct = default)
    {
        if (!Supported)
            return new(false, "Dynamo is not running as a Windows service here, so it cannot replace itself: " +
                "download the new version and install it by hand.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new(false, "An update is already in progress.");
        try
        {
            return await RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The update failed before the files were replaced");
            return new(false, "The update could not be prepared: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<UpdateOutcome> RunAsync(CancellationToken ct)
    {
        // Ask GitHub again: what the page showed may be a day old.
        var status = await checker.CheckAsync(ct);
        if (status.Latest is not { } latest || !checker.Available)
            return new(false, status.Error ?? "Dynamo is already up to date.");

        var text = UpdateChecker.ToText(latest);
        var url = latest.DownloadUrl;
        if (url is null || !AssetAddress().IsMatch(url) || !url.EndsWith("/Dynamo-" + text + ".zip", StringComparison.Ordinal))
            return new(false, "The release has no download this program accepts (it must be this project's Dynamo-" +
                text + ".zip on GitHub).");
        if (latest.Sha256 is not { Length: 64 } expected || !expected.All(Uri.IsHexDigit))
            return new(false, "The release publishes no SHA-256 checksum for its zip, so it will not be installed " +
                "automatically. Download it by hand instead.");

        // A clean staging folder.
        var staging = UpdateFolder;
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var zip = Path.Combine(staging, "Dynamo-" + text + ".zip");

        var actual = await DownloadAsync(url, zip, ct);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            return new(false, "The downloaded file does not match the published SHA-256 checksum: it was not installed.");
        }

        var files = Path.Combine(staging, "files");
        ZipFile.ExtractToDirectory(zip, files);
        var exe = Directory.EnumerateFiles(files, "Dynamo.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "app", StringComparison.OrdinalIgnoreCase));
        if (exe is null)
            return new(false, "The downloaded package does not contain the program (app\\Dynamo.exe).");
        if (_readVersion(exe) is not { } built || AppVersion.Format(built) != text)
            return new(false, "The program in the package is not version " + text + ": it was not installed.");

        var script = Path.Combine(staging, "apply-update.ps1");
        File.WriteAllText(script, ReadScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (File.Exists(ResultFile))
            File.Delete(ResultFile);

        var installFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        launcher.Start(script,
        [
            "-ServicePid", Environment.ProcessId.ToString(),
            "-Source", Path.GetDirectoryName(exe)!,
            "-InstallDir", installFolder,
            "-DataDir", home.Path,
            "-Port", options.HttpPort.ToString(),
            "-OldVersion", AppVersion.Format(checker.Current),
            "-NewVersion", text,
            "-Log", LogFile,
        ]);
        log.LogInformation("Updating {Old} -> {New}: the service is about to restart", checker.Current, text);
        return new(true, null, text);
    }

    /// <summary>Downloads to a file, stopping at the size limit; returns the SHA-256 (hex).</summary>
    private async Task<string> DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var client = http.CreateClient(DownloadClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidOperationException("The file is larger than expected.");

        using var hash = SHA256.Create();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidOperationException("The file is larger than expected.");
            hash.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    private static string ReadScript()
    {
        using var stream = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("apply-update.ps1")
            ?? throw new InvalidOperationException("The update script is missing from the program.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Version? ProductVersionOf(string exe)
    {
        var text = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
        return Version.TryParse(text, out var v) ? v : null;
    }
}
