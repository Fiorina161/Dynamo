using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dynamo.Core;

/// <summary>
/// Opens a file, a folder or a protocol (mailto:, vscode://...) with its Windows
/// default handler, <b>on the desktop of the person using the computer</b>.
///
/// Why this exists: a Windows service runs in session 0, which has no desktop. A
/// program it starts with Process.Start runs there, invisible, so a bookmark
/// "does nothing". From a service, the program is therefore started in the
/// signed-in user's session with their own token (WTSQueryUserToken +
/// CreateProcessAsUser), which needs the right a LocalSystem service has. Run
/// as a console, the user's own session is already the current one and
/// Process.Start does the job.
/// </summary>
public static class DesktopLauncher
{
    private const uint NoSession = 0xFFFFFFFF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const int ErrorPrivilegeNotHeld = 1314;
    private const int ErrorNoToken = 1008;

    /// <summary>How long to wait for the shell's "start" to report an error.</summary>
    private const int ReportWaitMilliseconds = 10_000;

    /// <summary>True in a Windows service (session 0), which has no desktop.</summary>
    public static bool InServiceSession
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId == 0;
        }
    }

    /// <summary>
    /// Opens the target on the user's desktop. Returns null on success, otherwise a
    /// message the user can read.
    /// </summary>
    public static string? Open(string target)
    {
        try
        {
            return InServiceSession ? OpenFromService(target) : OpenDirect(target);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? OpenDirect(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        return null;
    }

    /// <summary>
    /// What <c>cmd.exe</c> is given: <c>start</c> opens anything the shell can, and
    /// the empty "" is the window title it would otherwise take the target for.
    /// </summary>
    internal static string CommandLineFor(string cmdPath, string target) =>
        $"\"{cmdPath}\" /c start \"\" \"{target}\"";

    /// <summary>Why a target cannot be handed to the shell this way, or null.</summary>
    internal static string? Refusal(string target)
    {
        if (target.Length == 0)
            return "Nothing to open.";
        // A quote would end the quoted target; line breaks would end the command.
        if (target.Any(c => c is '"' or '\r' or '\n' or '\0'))
            return "This target contains a character that cannot be opened from the service.";
        return null;
    }

    /// <summary>
    /// Runs <paramref name="action"/> for the person using the computer. From a service that is with their own
    /// token (the primary token handle given to the action, valid only during the call: use it to impersonate them or
    /// to start a program as them); run as a console the current user already is that person and the action gets
    /// <see cref="IntPtr.Zero"/>. The action's text is the error to show, or null. Used where something has to happen in the user's
    /// own account (their Windows credential store, a program on their desktop).
    /// </summary>
    public static string? WithUserSession(Func<IntPtr, string?> action)
    {
        try
        {
            return InServiceSession ? WithServiceUserToken(action) : action(IntPtr.Zero);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? WithServiceUserToken(Func<IntPtr, string?> action)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession)
            return "No user is signed in on this computer.";
        if (!WTSQueryUserToken(session, out var userToken))
        {
            var error = Marshal.GetLastWin32Error();
            return error switch
            {
                ErrorNoToken => "No user is signed in on this computer.",
                ErrorPrivilegeNotHeld =>
                    "The Dynamo service may not act as the signed-in user. Reinstall it as LocalSystem " +
                    "(install-service.ps1 -LocalSystem) or run Dynamo as a console.",
                _ => $"Could not reach your session: {new Win32Exception(error).Message}",
            };
        }
        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                return $"Could not reach your session: {new Win32Exception().Message}";
            try
            {
                return action(primary);
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private static string? OpenFromService(string target)
    {
        if (Refusal(target) is { } refusal)
            return refusal;

        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession)
            return "No user is signed in on this computer.";

        if (!WTSQueryUserToken(session, out var userToken))
        {
            var error = Marshal.GetLastWin32Error();
            return error switch
            {
                ErrorNoToken => "No user is signed in on this computer.",
                ErrorPrivilegeNotHeld =>
                    "The Dynamo service may not start programs on your desktop. Reinstall it " +
                    "as LocalSystem (install-service.ps1 -LocalSystem) or run Dynamo as a console.",
                _ => $"Could not reach your desktop: {new Win32Exception(error).Message}",
            };
        }

        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out var primary))
                return $"Could not reach your desktop: {new Win32Exception().Message}";

            try
            {
                var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var folder = Path.IsPathRooted(target) ? Path.GetDirectoryName(target) : null;
                var (error, exitCode) = RunAsUser(
                    primary, cmd, CommandLineFor(cmd, target),
                    folder is not null && Directory.Exists(folder) ? folder : null,
                    ReportWaitMilliseconds);
                if (error is not null)
                    return error;
                return exitCode is null or 0
                    ? null
                    : $"Windows could not open it (code {exitCode}).";
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Starts <paramref name="application"/> with the given primary token, on the
    /// interactive desktop, without a console window, and waits up to
    /// <paramref name="waitMilliseconds"/> for it to end. The exit code is null if it
    /// was still running (a program that stays open is a success).
    /// </summary>
    public static (string? Error, uint? ExitCode) RunAsUser(
        IntPtr primaryToken, string application, string commandLine, string? folder, int waitMilliseconds)
    {
        // The user's own environment (PATH, profile folders), not the service's.
        if (!CreateEnvironmentBlock(out var environment, primaryToken, false))
            environment = IntPtr.Zero;

        try
        {
            var startup = new Startupinfo
            {
                cb = Marshal.SizeOf<Startupinfo>(),
                lpDesktop = @"winsta0\default",
            };

            var created = CreateProcessAsUser(
                primaryToken, application, new StringBuilder(commandLine),
                IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment | CreateNoWindow,
                environment, folder, ref startup, out var process);
            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                return (error == ErrorPrivilegeNotHeld
                    ? "The Dynamo service may not start programs on your desktop. Reinstall it " +
                      "as LocalSystem (install-service.ps1 -LocalSystem) or run Dynamo as a console."
                    : $"Could not start the program on your desktop: {new Win32Exception(error).Message}",
                    null);
            }

            try
            {
                if (WaitForSingleObject(process.hProcess, (uint)waitMilliseconds) == WaitTimeout)
                    return (null, null);
                return GetExitCodeProcess(process.hProcess, out var code) ? (null, code) : (null, null);
            }
            finally
            {
                CloseHandle(process.hProcess);
                CloseHandle(process.hThread);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startupinfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref Startupinfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}
