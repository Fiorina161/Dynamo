using System.Security.Principal;
using Dynamo.Core;

namespace Dynamo.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class DesktopLauncherTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    [Test]
    public void CommandLine_UsesStartWithAnEmptyTitle_AndQuotesTheTarget()
    {
        Assert.That(
            DesktopLauncher.CommandLineFor(Cmd, @"C:\My Docs\a b.txt"),
            Is.EqualTo(@"""C:\Windows\System32\cmd.exe"" /c start """" ""C:\My Docs\a b.txt"""));
    }

    [Test]
    public void CommandLine_KeepsAmpersandsAndUrlsInsideTheQuotes()
    {
        var line = DesktopLauncher.CommandLineFor(Cmd, "https://x.test/?a=1&b=2");

        Assert.That(line, Does.EndWith("start \"\" \"https://x.test/?a=1&b=2\""));
    }

    [TestCase("")]
    [TestCase("C:\\a\"b.txt")]
    [TestCase("C:\\a\r\nb.txt")]
    [TestCase("C:\\a\nb.txt")]
    [TestCase("C:\\a\0b.txt")]
    public void Refusal_StopsTargetsThatCouldBreakOutOfTheCommand(string target)
    {
        Assert.That(DesktopLauncher.Refusal(target), Is.Not.Null);
    }

    [TestCase(@"C:\Users\me\My Documents\report (final) & notes.docx")]
    [TestCase(@"\\server\share\folder")]
    [TestCase("mailto:someone@example.com")]
    [TestCase("notepad.exe")]
    public void Refusal_AllowsOrdinaryTargets(string target)
    {
        Assert.That(DesktopLauncher.Refusal(target), Is.Null);
    }

    [Test]
    public void ARegularTestRun_IsNotInTheServiceSession()
    {
        Assert.That(DesktopLauncher.InServiceSession, Is.False);
    }

    // ---- The native part, with a copy of this process's own token ----------------
    // The real service path (WTSQueryUserToken) needs LocalSystem; what can be checked
    // anywhere is everything after the token: the structures handed to Windows, the
    // process creation and the reporting of how it ended.

    private static IntPtr OwnPrimaryToken()
    {
        var self = WindowsIdentity.GetCurrent().AccessToken.DangerousGetHandle();
        if (!DesktopLauncher.DuplicateTokenEx(self, 0x02000000, IntPtr.Zero, 2, 1, out var primary))
            Assert.Inconclusive("Could not duplicate this process's token here.");
        return primary;
    }

    private static (string? Error, uint? ExitCode) Run(string arguments, int waitMs)
    {
        var token = OwnPrimaryToken();
        try
        {
            var result = DesktopLauncher.RunAsUser(token, Cmd, $"\"{Cmd}\" {arguments}", null, waitMs);
            if (result.Error is not null && result.Error.Contains("may not start programs"))
                Assert.Inconclusive("This account may not call CreateProcessAsUser: " + result.Error);
            return result;
        }
        finally
        {
            DesktopLauncher.CloseHandle(token);
        }
    }

    [Test]
    public void RunAsUser_StartsTheProgram_AndReportsItsExitCode()
    {
        var (error, exitCode) = Run("/c exit 7", 10_000);

        Assert.That(error, Is.Null);
        Assert.That(exitCode, Is.EqualTo(7u));
    }

    [Test]
    public void RunAsUser_ASuccessfulProgramReportsZero()
    {
        var (error, exitCode) = Run("/c exit 0", 10_000);

        Assert.That(error, Is.Null);
        Assert.That(exitCode, Is.EqualTo(0u));
    }

    [Test]
    public void RunAsUser_AProgramStillRunningIsASuccess_WithNoExitCode()
    {
        // Something that stays open (a document viewer) must not be reported as a failure.
        var (error, exitCode) = Run("/c ping -n 3 127.0.0.1 >nul", 100);

        Assert.That(error, Is.Null);
        Assert.That(exitCode, Is.Null);
    }

    [Test]
    public void RunAsUser_ThePathToTheProgramThatDoesNotExist_IsAnError()
    {
        var token = OwnPrimaryToken();
        try
        {
            var (error, exitCode) = DesktopLauncher.RunAsUser(
                token, @"C:\no\such\program.exe", @"""C:\no\such\program.exe""", null, 1_000);
            if (error is not null && error.Contains("may not start programs"))
                Assert.Inconclusive(error);

            Assert.That(error, Does.StartWith("Could not start the program on your desktop"));
            Assert.That(exitCode, Is.Null);
        }
        finally
        {
            DesktopLauncher.CloseHandle(token);
        }
    }
}
