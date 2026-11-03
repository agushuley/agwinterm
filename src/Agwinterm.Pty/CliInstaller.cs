namespace Agwinterm.Pty;

/// <summary>
/// Opt-in installer that puts <c>agwintermctl</c> on the user's PATH (HKCU\Environment) so it's
/// callable from any shell. Mirrors agterm's "Install Command Line Tool" Help-menu action — kept
/// OUT of the app installer on purpose (minimal, non-invasive setup; the user opts in from the app).
/// Uses the User-scoped environment API, which persists to HKCU and broadcasts WM_SETTINGCHANGE so
/// newly launched shells pick up the change.
/// </summary>
public static class CliInstaller
{
    /// <summary>The directory holding agwintermctl.exe (it sits next to Agwinterm.Win32.exe / the ctl exe).</summary>
    public static string CliDir() => PortableTools.DirectoryPath;

    /// <summary>Extract the portable toolset without opening the terminal or modifying PATH.</summary>
    public static void ExtractPortableTools(string root) => PortableTools.ExtractEmbedded(root);

    private static string[] UserPathParts()
    {
        string cur = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        return cur.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static string[] UpdatedPath(string[] parts, string dir, string portableRoot, bool remove)
    {
        bool IsPortable(string path)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path)) return false;
                string full = Path.GetFullPath(path).TrimEnd('\\', '/');
                string name = Path.GetFileName(full);
                return name.Length == 64 && name.All(Uri.IsHexDigit)
                    && SameDir(Path.GetDirectoryName(full) ?? "", Path.GetFullPath(portableRoot));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        bool portable = IsPortable(dir);
        var result = new List<string>();
        bool inserted = false;
        foreach (string part in parts)
        {
            if (!SameDir(part, dir) && !(portable && IsPortable(part))) { result.Add(part); continue; }
            if (!remove && !inserted) { result.Add(dir); inserted = true; }
        }
        if (!remove && !inserted) result.Add(dir);
        return result.ToArray();
    }

    /// <summary>Add the ctl directory to the user PATH (idempotent). Returns a human-readable status.</summary>
    public static string Install()
    {
        string dir = CliDir();
        try
        {
            var parts = UserPathParts();
            var updated = UpdatedPath(parts, dir, PortableTools.CacheRoot, remove: false);
            if (parts.SequenceEqual(updated, StringComparer.OrdinalIgnoreCase))
                return "agwintermctl is already on your PATH -> " + dir;
            Environment.SetEnvironmentVariable("Path", string.Join(";", updated),
                EnvironmentVariableTarget.User);
            return "added agwintermctl to your PATH -> " + dir + " (open a new shell to use it)";
        }
        catch (Exception ex) { return "failed to add to PATH: " + ex.Message; }
    }

    /// <summary>Remove the ctl directory from the user PATH. Returns a human-readable status.</summary>
    public static string Uninstall()
    {
        string dir = CliDir();
        try
        {
            var parts = UserPathParts();
            var kept = UpdatedPath(parts, dir, PortableTools.CacheRoot, remove: true);
            if (kept.Length == parts.Length) return "agwintermctl was not on your PATH";
            Environment.SetEnvironmentVariable("Path", string.Join(";", kept), EnvironmentVariableTarget.User);
            return "removed agwintermctl from your PATH";
        }
        catch (Exception ex) { return "failed to remove from PATH: " + ex.Message; }
    }
}
