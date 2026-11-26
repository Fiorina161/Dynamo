using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Dynamo.Core;
using Microsoft.Win32.SafeHandles;

namespace Dynamo.Modules.RemoteDesktops;

/// <summary>
/// The Windows Credential Manager (advapi32 CredWrite / CredRead / CredDelete), for the person whose token is given:
/// the call is made while impersonating them, so the credential goes into <b>their</b> store and not the service's
/// (a LocalSystem service has a store of its own that no one else looks in). With no token (a console run) the current
/// user is the one. A "generic" credential named <c>TERMSRV/host</c> is what the Remote Desktop client looks for, the
/// same as <c>cmdkey /generic:TERMSRV/host</c>; the password never goes through a command line.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCredentials
{
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>Runs <paramref name="work"/> as the person whose primary token this is (or as the current user for zero).</summary>
    private static T As<T>(IntPtr primaryToken, Func<T> work)
    {
        if (primaryToken == IntPtr.Zero)
            return work();
        // An impersonation token of their own, so closing it here does not touch the caller's.
        if (!DesktopLauncher.DuplicateTokenEx(primaryToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out var copy))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var handle = new SafeAccessTokenHandle(copy);
        return WindowsIdentity.RunImpersonated(handle, work);
    }

    /// <summary>Saves (or replaces) the credential. Returns null, or why it could not.</summary>
    public static string? Write(IntPtr token, string target, string username, string password) =>
        As(token, () =>
        {
            var blob = Marshal.StringToCoTaskMemUni(password);
            try
            {
                var credential = new Credential
                {
                    Type = TypeGeneric,
                    TargetName = target,
                    Comment = "Saved by Dynamo",
                    CredentialBlobSize = (uint)(password.Length * 2),
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = username,
                };
                return CredWrite(ref credential, 0) ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        });

    /// <summary>Removes the credential. Found is false when there was none (which is not an error).</summary>
    public static string? Delete(IntPtr token, string target, out bool found)
    {
        var existed = true;
        var error = As(token, () =>
        {
            if (CredDelete(target, TypeGeneric, 0))
                return null;
            var code = Marshal.GetLastWin32Error();
            if (code == ErrorNotFound)
            {
                existed = false;
                return null;
            }
            return new Win32Exception(code).Message;
        });
        found = existed;
        return error;
    }

    /// <summary>The saved user name and password, or null (used by the tests to check what Connect saved).</summary>
    public static (string User, string Password)? Read(IntPtr token, string target) =>
        As<(string, string)?>(token, () =>
        {
            if (!CredRead(target, TypeGeneric, 0, out var buffer))
                return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(buffer);
                var password = credential.CredentialBlobSize == 0
                    ? ""
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? "";
                return (credential.UserName ?? "", password);
            }
            finally
            {
                CredFree(buffer);
            }
        });
}

/// <summary>Opens a remote desktop for the person using this computer; an interface so the pages can be tested.</summary>
public interface IRemoteDesktopLauncher
{
    /// <summary>Saves the credential in their Windows store and starts the client. Null on success, else why not.</summary>
    string? Connect(RemoteDesktop desktop);

    /// <summary>Removes the credential Windows saved for this host. Null on success (even if there was none).</summary>
    string? Forget(RemoteDesktop desktop);
}

/// <summary>
/// Both steps happen in the signed-in user's own account (<see cref="DesktopLauncher.WithUserSession"/>): the saved
/// credential goes into their Credential Manager, then <c>mstsc.exe /v:host</c> starts on their desktop and finds it,
/// so it connects without asking. Every Connect saves the credential again, so a password changed in Dynamo takes
/// effect at once.
/// </summary>
public sealed class RemoteDesktopLauncher : IRemoteDesktopLauncher
{
    public string? Connect(RemoteDesktop desktop) =>
        DesktopLauncher.WithUserSession(token =>
            OperatingSystem.IsWindows() ? ConnectAs(token, desktop) : "Remote desktops need Windows.");

    [SupportedOSPlatform("windows")]
    private static string? ConnectAs(IntPtr token, RemoteDesktop desktop)
    {
        var error = WindowsCredentials.Write(token, RemoteDesktopHost.CredentialTarget(desktop.Host), desktop.Username, desktop.Password);
        if (error is not null)
            return "Could not save the credentials in your Windows Credential Manager: " + error;

        var mstsc = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        if (token == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo(mstsc, "/v:" + desktop.Host) { UseShellExecute = false })?.Dispose();
            return null;
        }
        // The host was validated (letters, digits, dots, hyphens, a port): nothing in it can end the command line.
        var (start, code) = DesktopLauncher.RunAsUser(token, mstsc, $"\"{mstsc}\" /v:{desktop.Host}", null, 1500);
        return start ?? (code is null or 0 ? null : $"The Remote Desktop client stopped at once (code {code}).");
    }

    public string? Forget(RemoteDesktop desktop) =>
        DesktopLauncher.WithUserSession(token =>
            OperatingSystem.IsWindows()
                ? WindowsCredentials.Delete(token, RemoteDesktopHost.CredentialTarget(desktop.Host), out _)
                : "Remote desktops need Windows.");
}
