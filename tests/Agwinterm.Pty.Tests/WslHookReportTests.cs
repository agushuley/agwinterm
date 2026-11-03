using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslHookReportTests
{
    private static readonly ShellProfile Profile = new() { Command = "wsl.exe", Args = ["-d", "Ubuntu"] };
    private static readonly AgentResume.ProcRow Row = new(42, 1, "codex", "\"/bin/codex\" \"--sandbox\" \"workspace-write\"", 12345);
    private static byte[] Read(string path) => Encoding.UTF8.GetBytes(Path.GetFileName(path) switch
    {
        "environ" => "HOME=/tmp\0AGWINTERM_SESSION_ID=pane\0",
        "stat" => "42 (name with ) spaces) S 1 " + string.Join(' ', Enumerable.Repeat("0", 17)) + " 12345",
        "cmdline" => "/bin/codex\0--sandbox\0workspace-write\0",
        _ => throw new IOException(path)
    });

    [Fact]
    public void VerifiesLiveIdentityAndProcessSnapshot()
    {
        var report = new WslHookReport(1, "Ubuntu", [Row]);
        Assert.Equal(Row, report.Verify(Profile, 42, "pane", Read)[42]);
    }

    [Fact]
    public void RejectsWrongPaneDistributionAndTraversal()
    {
        Assert.Throws<InvalidDataException>(() => new WslHookReport(1, "Ubuntu", [Row]).Verify(Profile, 42, "other", Read));
        Assert.Throws<InvalidDataException>(() => new WslHookReport(1, "Other", [Row]).Verify(Profile, 42, "pane", Read));
        Assert.Throws<InvalidDataException>(() => new WslHookReport(1, @"Ubuntu\..\Other", [Row]).Verify(Profile, 42, "pane", Read));
    }

    [Fact]
    public void RejectsPidReuseForgedFlagsAndMissingAncestors()
    {
        foreach (var row in new[] { Row with { Created = 2 }, Row with { CommandLine = "\"codex\" \"--yolo\"" }, Row with { ParentPid = 3 }, Row with { Name = "claude" } })
            Assert.Throws<InvalidDataException>(() => new WslHookReport(1, "Ubuntu", [row]).Verify(Profile, 42, "pane", Read));
        Assert.Throws<InvalidDataException>(() => new WslHookReport(1, "Ubuntu", []).Verify(Profile, 42, "pane", Read));
        Assert.Throws<InvalidDataException>(() => new WslHookReport(2, "Ubuntu", [Row]).Verify(Profile, 42, "pane", Read));
    }
}
