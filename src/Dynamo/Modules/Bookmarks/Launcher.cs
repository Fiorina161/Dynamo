using System.Diagnostics;

namespace Dynamo.Modules.Bookmarks;

/// <summary>
/// Opens a bookmark's target on the machine running the service: a file or
/// folder with its Windows default handler, or any other protocol handler
/// (mailto:, vscode://...). The browser cannot do it itself: it refuses
/// file:// links from an http page.
/// </summary>
public static class Launcher
{
    public static bool IsWeb(string target) =>
        Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static string? Open(string target)
    {
        target = target.Trim();
        var path = LocalPathOf(target);
        if (path is not null && !File.Exists(path) && !Directory.Exists(path))
            return $"Not found: {path}";

        try
        {
            Process.Start(new ProcessStartInfo(path ?? target) { UseShellExecute = true })
                ?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The file system path the target designates, if it is one.</summary>
    private static string? LocalPathOf(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : null;
        // A bare command name (notepad.exe) is left to the shell to resolve.
        return Path.IsPathRooted(target) ? target : null;
    }
}
