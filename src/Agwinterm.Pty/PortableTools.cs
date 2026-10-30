using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Agwinterm.Pty;

/// <summary>Portable builds carry their CLI and WSL helpers inside the app executable.</summary>
internal static class PortableTools
{
    private static readonly string[] Files =
        ["agwintermctl.exe", "agwinterm-hook.exe", "agwinterm-hook-linux-x64", "agwinterm-hook-linux-arm64"];
    private static readonly Lazy<string> Location = new(Resolve);
    internal static string DirectoryPath => Location.Value;
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "agwinterm", "portable-tools");

    private static string Resolve()
    {
        using var payload = Payload();
        if (payload is null) return AppContext.BaseDirectory.TrimEnd('\\', '/');
        return Extract(payload, CacheRoot);
    }

    internal static void ExtractEmbedded(string root)
    {
        using var payload = Payload() ?? throw new InvalidOperationException("This build has no embedded portable tools.");
        Extract(payload, root);
    }

    private static Stream? Payload() => Assembly.GetEntryAssembly()?.GetManifestResourceStream("agwinterm.tools.zip");

    // Content-addressed directories keep installed WSL wrappers valid across app updates.
    // Publish the complete directory atomically: simultaneous app instances never see half a toolset.
    internal static string Extract(Stream payload, string root)
    {
        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        payload.Position = 0;
        string destination = Path.Combine(root, digest);
        if (Complete(destination)) return destination;
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            foreach (string name in Files)
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Portable payload is missing {name}");
                entry.ExtractToFile(Path.Combine(staging, name));
            }
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Complete(destination)) { } // Another instance published first.
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static bool Complete(string directory) => Files.All(name => File.Exists(Path.Combine(directory, name)));
}
