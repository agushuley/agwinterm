using System.Globalization;
using System.Text;

namespace Agwinterm.Pty;

/// <summary>A Linux helper's observation, verified against live procfs without starting wsl.exe.</summary>
public sealed record WslHookReport(int Version, string Distribution, AgentResume.ProcRow[] Processes)
{
    public IReadOnlyDictionary<int, AgentResume.ProcRow> Verify(ShellProfile profile, int pid, string pane)
        => Verify(profile, pid, pane, ReadBounded);

    internal IReadOnlyDictionary<int, AgentResume.ProcRow> Verify(ShellProfile profile, int pid, string pane, Func<string, byte[]> read)
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Distribution) || Distribution.Length > 128
            || Distribution is "." or ".." || Distribution.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Processes is not { Length: > 0 and <= 64 })
            throw new InvalidDataException("Invalid native WSL hook report");
        var selectors = WslIntegration.Selectors(profile.Args);
        for (int i = 0; i < selectors.Length; i++)
        {
            string? selected = selectors[i] is "-d" or "--distribution" ? selectors[++i]
                : selectors[i].StartsWith("--distribution=", StringComparison.Ordinal) ? selectors[i][15..] : null;
            if (selected is not null && !selected.Equals(Distribution, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hook distribution does not match the pane");
        }
        string root = @"\\wsl.localhost\" + Distribution + @"\proc\";
        string env = Encoding.UTF8.GetString(read(root + pid + @"\environ"));
        if (!env.Split('\0').Contains("AGWINTERM_SESSION_ID=" + pane, StringComparer.Ordinal))
            throw new InvalidDataException("Hook PID does not belong to the pane");
        var rows = new Dictionary<int, AgentResume.ProcRow>();
        int expected = pid;
        foreach (var row in Processes)
        {
            if (row is null || row.Pid != expected || row.Pid <= 1 || !rows.TryAdd(row.Pid, row))
                throw new InvalidDataException("Invalid hook ancestry chain");
            string dir = root + row.Pid + @"\";
            string stat = Encoding.UTF8.GetString(read(dir + "stat"));
            int end = stat.LastIndexOf(')');
            if (end < 0) throw new InvalidDataException("Invalid proc stat");
            string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 20 || !int.TryParse(fields[1], out int parent)
                || !long.TryParse(fields[19], CultureInfo.InvariantCulture, out long created)
                || row.ParentPid != parent || row.Created != created)
                throw new InvalidDataException("Hook process changed while binding");
            var argv = Encoding.UTF8.GetString(read(dir + "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            string name = argv.Length == 0 ? "" : argv[0].Split('/')[^1];
            string command = string.Join(' ', argv.Select(s => "\"" + s.Replace("\"", "\\\"") + "\""));
            if (row.Name != name || row.CommandLine != command)
                throw new InvalidDataException("Hook command line does not match procfs");
            expected = parent;
        }
        if (expected > 1) throw new InvalidDataException("Incomplete hook ancestry chain");
        return rows;
    }

    private static byte[] ReadBounded(string path)
    {
        // procfs files report length zero. Read the stream instead of trusting its Length.
        using var stream = File.OpenRead(path);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int n;
        while ((n = stream.Read(buffer)) != 0)
        {
            if (output.Length + n > 1024 * 1024) throw new InvalidDataException("procfs data too large");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
}
