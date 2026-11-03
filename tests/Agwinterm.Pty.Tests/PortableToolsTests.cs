using System.IO.Compression;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class PortableToolsTests
{
    [Fact]
    public void InstallingUpdatedPortableReplacesOldPathEntriesAndUninstallRemovesAllVersions()
    {
        string root = @"C:\Users\test\AppData\Local\agwinterm\portable-tools";
        string old = Path.Combine(root, new string('a', 64));
        string current = Path.Combine(root, new string('b', 64));
        string unrelated = Path.Combine(root + "-other", new string('c', 64));
        string nested = Path.Combine(old, "user-tools");
        string[] parts = [@"C:\first", old.ToUpperInvariant() + "\\", unrelated, nested, current, @"C:\last"];
        string[] expected = [@"C:\first", current, unrelated, nested, @"C:\last"];
        Assert.Equal(expected, CliInstaller.UpdatedPath(parts, current, root, remove: false));
        Assert.Equal(expected, CliInstaller.UpdatedPath(expected, current, root, remove: false));
        Assert.Equal(new[] { @"C:\first", unrelated, nested, @"C:\last" },
            CliInstaller.UpdatedPath(parts, current, root, remove: true));
        Assert.Equal(parts.Append(@"D:\installed"),
            CliInstaller.UpdatedPath(parts, @"D:\installed", root, remove: false));
    }

    private static readonly string[] Names =
        ["agwintermctl.exe", "agwinterm-hook.exe", "agwinterm-hook-linux-x64", "agwinterm-hook-linux-arm64"];

    private static byte[] Payload(string version, bool incomplete = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in incomplete ? Names.Take(2) : Names)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(version + name));
            }
        return buffer.ToArray();
    }

    [Fact]
    public async Task ConcurrentExtractionReusesCompletePayloadAndUpdatesKeepOldPathsValid()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = Payload("old");
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return PortableTools.Extract(stream, root);
            })));
            Assert.Single(results.Distinct());
            using var updated = new MemoryStream(Payload("new"));
            string next = PortableTools.Extract(updated, root);
            Assert.NotEqual(results[0], next);
            foreach (string name in Names)
            {
                Assert.Equal("old" + name, File.ReadAllText(Path.Combine(results[0], name)));
                Assert.Equal("new" + name, File.ReadAllText(Path.Combine(next, name)));
            }
            Assert.Equal(2, Directory.GetDirectories(root).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void IncompleteArchiveNeverPublishesPartialTools()
    {
        string root = Path.Combine(Path.GetTempPath(), "agwinterm-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new MemoryStream(Payload("bad", incomplete: true));
            Assert.Throws<InvalidDataException>(() => PortableTools.Extract(stream, root));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
