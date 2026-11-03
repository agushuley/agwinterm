using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class EnvironmentOverridesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("abc=def")]
    [InlineData("  with spaces  ")]
    [InlineData("\"abc=def\\n")]
    [InlineData("C:\\new\\tools")]
    [InlineData("$PATH ${TOKEN} %PATH% $(whoami) `cmd` #literal")]
    [InlineData("abc=def\nbcd=\nqw=")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("a\r")]
    [InlineData("a\r\n")]
    [InlineData("a\nb\r\nc\rd\n\n")]
    [InlineData("END_ENV\nEND_ENV_1\nEND_ENV_2")]
    [InlineData("Україна\n文字")]
    public void TextRoundTrip_PreservesLiteralValue(string value)
    {
        var original = new Dictionary<string, string> { ["TOKEN"] = value };
        var text = EnvironmentOverrides.Format(original);
        Assert.Equal(value, EnvironmentOverrides.Parse(text)["TOKEN"]);
        Assert.Equal(value, EnvironmentOverrides.ParseEditor(EnvironmentOverrides.FormatEditor(original), original)["TOKEN"]);
    }

    [Fact]
    public void EditingAnotherVariable_PreservesExistingLineEndings()
    {
        var original = new Dictionary<string, string> { ["TOKEN"] = "a\r\nb\r", ["OTHER"] = "old" };
        string text = EnvironmentOverrides.FormatEditor(original).Replace("OTHER=old", "OTHER=new");
        var edited = EnvironmentOverrides.ParseEditor(text, original);
        Assert.Equal(original["TOKEN"], edited["TOKEN"]);
        Assert.Equal("new", edited["OTHER"]);
    }

    [Fact]
    public void Heredoc_UsesExactMarkerAndRetainsBlankLines()
    {
        var values = EnvironmentOverrides.Parse("TOKEN<<END\n END\n\nEND\nOTHER=x=y\n");
        Assert.Equal(" END\n", values["TOKEN"]);
        Assert.Equal("x=y", values["OTHER"]);
    }

    [Theory]
    [InlineData("TOKEN")]
    [InlineData("TOKEN<<END\nsecret")]
    [InlineData("TOKEN<<\nsecret")]
    [InlineData("TOKEN=a\ntoken=b")]
    [InlineData("TOKEN=a\nINVALID NAME=secret")]
    [InlineData("TOKEN=secret\0")]
    [InlineData("AGWINTERM_PIPE=secret")]
    [InlineData("AGW_TARGET=secret")]
    [InlineData("TERM_PROGRAM=secret")]
    public void InvalidText_ReportsLineWithoutLeakingValue(string text)
    {
        var error = Assert.Throws<FormatException>(() => EnvironmentOverrides.Parse(text));
        Assert.StartsWith("Line ", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void Merge_LastLayerWinsWithoutMutatingInputs()
    {
        var global = new Dictionary<string, string> { ["TOKEN"] = "global", ["GLOBAL"] = "yes" };
        var profile = new Dictionary<string, string> { ["token"] = "profile", ["PROFILE"] = "yes" };
        var workspace = new Dictionary<string, string> { ["Token"] = "workspace" };
        var session = new Dictionary<string, string> { ["TOKEN"] = "" };
        var merged = EnvironmentOverrides.Merge(global, profile, workspace, session);
        Assert.Equal("", merged["token"]);
        Assert.Equal("yes", merged["GLOBAL"]);
        Assert.Equal("yes", merged["PROFILE"]);
        Assert.Equal("global", global["TOKEN"]);
        Assert.Equal("workspace", workspace["Token"]);
    }

    [Fact]
    public void Snapshot_OwnsItsValues()
    {
        var source = new Dictionary<string, string> { ["TOKEN"] = "old" };
        var snapshot = EnvironmentOverrides.Snapshot(source);
        source["TOKEN"] = "new";
        Assert.Equal("old", snapshot["TOKEN"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot)["TOKEN"] = "changed");
    }

    [Theory]
    [InlineData("{\"TOKEN\":null}")]
    [InlineData("{\"TOKEN\":1}")]
    [InlineData("{\"TOKEN\":\"a\",\"token\":\"b\"}")]
    [InlineData("[]")]
    public void Json_RejectsInvalidValuesAndDuplicateNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<FormatException>(() => EnvironmentOverrides.ReadJson(document.RootElement));
    }

    [Fact]
    public void JsonFile_RoundTripsValuesAndRejectsPartialUpdate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "agwinterm-env-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "environment.json");
        try
        {
            var original = new Dictionary<string, string> { ["TOKEN"] = "abc=def\nbcd=\nqw=", ["ROOT"] = @"C:\new\tools" };
            EnvironmentOverrides.Save(path, original);
            Assert.Equal(original["TOKEN"], EnvironmentOverrides.Load(path)["TOKEN"]);
            Assert.Throws<FormatException>(() => EnvironmentOverrides.Save(path, new Dictionary<string, string> { ["BAD NAME"] = "secret" }));
            Assert.Equal(original["ROOT"], EnvironmentOverrides.Load(path)["ROOT"]);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SessionNew_EnvIsValidatedBeforeCreatingAnything()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        using var reply = JsonDocument.Parse(server.Dispatch("""
            {"cmd":"session.new","args":{"workspace-name":"new","create-workspace":true,"env":{"TOKEN":null}}}
            """));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(host.Workspaces);
        Assert.Null(host.LastSessionRequest);
    }

    [Fact]
    public void SessionNew_EnvArrivesAsLiteralRequestOverrides()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        using var reply = JsonDocument.Parse(server.Dispatch("""
            {"cmd":"session.new","args":{"env":{"TOKEN":"abc=def\nbcd=\nqw="}}}
            """));
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("abc=def\nbcd=\nqw=", host.LastSessionRequest!.Env!["TOKEN"]);
        Assert.DoesNotContain("abc=def", server.Dispatch("{\"cmd\":\"tree\"}"));
    }

    [Fact]
    public void WorkspaceAndSessionState_PreserveOverridesAndLegacyDirectory()
    {
        const string legacy = """{"Workspaces":[{"Id":"w","Name":"project","DefaultDir":"C:\\repo","Sessions":[]}]}""";
        Assert.True(RestoreState.TryDeserialize(legacy, out var state));
        var workspace = state!.Workspaces[0];
        Assert.Equal(@"C:\repo", workspace.Options.DefaultDir);
        workspace.Options = workspace.Options with { DefaultProfile = "PowerShell 7", Env = EnvironmentOverrides.Parse("TOKEN=a=b") };
        workspace.Sessions.Add(new SessionState { Env = EnvironmentOverrides.Parse("TOKEN=session") });
        Assert.True(RestoreState.TryDeserialize(RestoreState.Serialize(state), out var restored));
        Assert.Equal("PowerShell 7", restored!.Workspaces[0].Options.DefaultProfile);
        Assert.Equal("a=b", restored.Workspaces[0].Options.Env!["TOKEN"]);
        Assert.Equal("session", restored.Workspaces[0].Sessions[0].Env!["TOKEN"]);
    }
}
