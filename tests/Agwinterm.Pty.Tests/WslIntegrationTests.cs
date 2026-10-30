using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslIntegrationTests
{
    [Theory]
    [InlineData("file://localhost/home/me/a%20b/%D1%82%D0%B5%D1%81%D1%82", "/home/me/a b/тест")]
    [InlineData("file://HOST/C:/Users/a%20b", @"C:\Users\a b")]
    [InlineData("file:///home/me/100%25", "/home/me/100%")]
    [InlineData("/home/me/literal%20name", "/home/me/literal%20name")]
    [InlineData("/", "/")]
    public void CwdPreservesLinuxPathsAndDecodesOnlyUris(string input, string expected)
        => Assert.Equal(expected, WslIntegration.DecodeCwd(input));

    [Fact]
    public void LaunchMovesCwdToWslWithoutTouchingCommandArguments()
    {
        string[] args = ["-d", "Ubuntu", "--cd", "/old", "--exec", "bash", "-c", "echo --cd"];
        Assert.Equal(new[] { "--cd", "/home/a b", "-d", "Ubuntu", "--exec", "bash", "-c", "echo --cd" },
            WslIntegration.LaunchArgs(args, "/home/a b"));
        Assert.Equal("/old", args[3]);
    }

    [Fact]
    public void HelpersUseTheProfilesDistroAndUserButNotItsCommand()
        => Assert.Equal(new[] { "--distribution", "My Ubuntu", "-u", "alice" },
            WslIntegration.Selectors(["--distribution", "My Ubuntu", "-u", "alice", "--cd", "/work", "-e", "bash", "-c", "-d other"]));

    [Theory]
    [InlineData("--cd", "/work space")]
    [InlineData("--cd=/work space", null)]
    public void ProfileDirectoryTakesPrecedenceOverHomeFallback(string option, string? value)
    {
        string[] args = value is null ? ["-d", "Ubuntu", option] : ["-d", "Ubuntu", option, value];
        var profile = new ShellProfile { Command = "wsl.exe", Args = args };
        string cwd = WslIntegration.ProfileDirectory(profile) ?? "~";
        Assert.Equal("/work space", cwd);
        Assert.Equal(new[] { "--cd", "/work space", "-d", "Ubuntu" }, WslIntegration.LaunchArgs(args, cwd));
        Assert.Equal(new[] { "--cd", "/restored", "-d", "Ubuntu" }, WslIntegration.LaunchArgs(args, "/restored"));
        profile.Cwd = "/configured";
        Assert.Equal("/configured", WslIntegration.ProfileDirectory(profile));
    }

    [Theory]
    [InlineData("--exec")]
    [InlineData("-e")]
    [InlineData("--")]
    [InlineData("bash")]
    public void CommandArgumentsCannotSetProfileDirectory(string command)
    {
        var profile = new ShellProfile { Command = "wsl.exe", Args = ["-d", "Ubuntu", command, "--cd", "/command-only"] };
        Assert.Null(WslIntegration.ProfileDirectory(profile));
    }

    [Fact]
    public void ProfileDirectoryIgnoresOtherOptionValuesAndWindowsArguments()
    {
        var profile = new ShellProfile { Command = "wsl.exe", Args = ["-d", "--cd=/distro-name", "-u", "--cd=/user-name"] };
        Assert.Null(WslIntegration.ProfileDirectory(profile));
        profile.Command = "powershell.exe";
        profile.Args = ["--cd", "/not-wsl"];
        Assert.Null(WslIntegration.ProfileDirectory(profile));
        profile.Cwd = @"C:\work";
        Assert.Equal(@"C:\work", WslIntegration.ProfileDirectory(profile));
    }

    [Fact]
    public void LinuxHomeOverrideIsAppliedAfterWslInitializesItsEnvironment()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/tmp/test home" };
        Assert.Equal(new[] { "-d", "Ubuntu", "--exec", "env", "--", "HOME=/tmp/test home", "bash", "-i" },
            WslIntegration.LaunchArgs(["-d", "Ubuntu", "--exec", "bash", "-i"], null, env));
    }

    [Fact]
    public void ProfileEnvironmentCannotReplacePaneIdentityOrTheMergedWslEnv()
    {
        var env = new Dictionary<string, string> { ["AGWINTERM_PIPE"] = "wrong", ["WSLENV"] = "HOME", ["HOME"] = "/tmp/test" };
        var args = WslIntegration.LaunchArgs(["--exec", "bash"], null, env);
        Assert.DoesNotContain("AGWINTERM_PIPE=wrong", args);
        Assert.DoesNotContain("WSLENV=HOME", args);
        Assert.Contains("HOME=/tmp/test", args);
    }

    [Fact]
    public void ImplicitWslCommandKeepsItsOwnArgumentsAndReceivesEnvironment()
    {
        string[] args = ["-d", "Ubuntu", "zsh", "-c", "echo --cd", "-u", "not-a-wsl-user"];
        Assert.Equal(new[] { "-d", "Ubuntu" }, WslIntegration.Selectors(args));
        Assert.Equal(new[] { "--cd", "/work", "-d", "Ubuntu", "--exec", "env", "--", "HOME=/tmp/test", "zsh", "-c", "echo --cd", "-u", "not-a-wsl-user" },
            WslIntegration.LaunchArgs(args, "/work", new Dictionary<string, string> { ["HOME"] = "/tmp/test" }));
    }

    [Fact]
    public void WslCommandsPreserveSelectorsAndShellMetacharacters()
    {
        var profile = new ShellProfile { Command = "wsl.exe", Args = ["-d", "Ubuntu", "-u", "alice", "--exec", "bash"] };
        Assert.Equal(new[] { "--cd", "/work", "-d", "Ubuntu", "-u", "alice", "--exec", "/bin/sh", "-lc", "printf '%s' \"$HOME\"" },
            WslIntegration.CommandArgs(profile, "printf '%s' \"$HOME\"", "/work", false));
        Assert.Equal("'a'\\''b'", WslIntegration.QuoteShell("a'b"));
    }

    [Fact]
    public void EnvironmentPreservesUserFlagsAndRepairsIdentityForInteropRoundtrip()
    {
        var env = new Dictionary<string, string>
        {
            ["WSLENV"] = "CUSTOM/p:AGWINTERM_PIPE/u:AGWINTERM_SESSION_ID/p",
            ["AGWINTERM_PIPE"] = "private-pipe", ["AGWINTERM_SESSION_ID"] = "pane-1",
            ["TERM_PROGRAM"] = "agwinterm", ["CUSTOM"] = @"C:\work"
        };
        WslIntegration.ExportEnvironment(env);
        string once = env["WSLENV"];
        WslIntegration.ExportEnvironment(env);
        Assert.Equal(once, env["WSLENV"]);
        Assert.Equal("CUSTOM/p:AGWINTERM_PIPE:AGWINTERM_SESSION_ID:TERM_PROGRAM", once);
    }

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("devin", "devin")]
    public void LinuxAgentsAreRecognized(string name, string agent)
        => Assert.Equal(agent, AgentResume.AgentOf(new(1, 0, name, name)));

    [Fact]
    public void LinuxNestedAgentCannotReplaceOuterResume()
    {
        var rows = new[] { new AgentResume.ProcRow(10, 9, "python3", "hook.py"),
            new AgentResume.ProcRow(9, 8, "codex", "codex"), new AgentResume.ProcRow(8, 1, "claude", "claude") };
        Assert.Null(AgentResume.FindAgentCommandLine(rows.ToDictionary(r => r.Pid), 10, 0, "codex", out var reason));
        Assert.StartsWith("nested:", reason);
    }

    [Fact]
    public void ShellResourceIsShippedWithoutPythonFallbacks()
    {
        Assert.NotEmpty(WslIntegration.Resource("shell.sh"));
        Assert.DoesNotContain(typeof(WslIntegration).Assembly.GetManifestResourceNames(), name => name.StartsWith("Agwinterm.Pty.Wsl.") && name.EndsWith(".py"));
    }
}
