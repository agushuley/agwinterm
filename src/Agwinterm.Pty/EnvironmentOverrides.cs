using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public static class EnvironmentOverrides
{
    public static IReadOnlyDictionary<string, string> Empty { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, string> Snapshot(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (name.Length == 0 || name.Any(character => char.IsWhiteSpace(character) || character is '=' or '\0' or '<'))
                throw new FormatException("Environment variable name is empty or contains whitespace, =, < or NUL.");
            if (name.StartsWith("AGWINTERM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("AGW_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TERM_PROGRAM", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TERM_PROGRAM_VERSION", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Environment variable name is reserved by agwinterm.");
            if (value is null || value.Contains('\0'))
                throw new FormatException("Environment values must be strings without NUL.");
            if (!result.TryAdd(name, value)) throw new FormatException("Duplicate environment variable name.");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    public static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>?[] layers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
            if (layer is not null)
                foreach (var (name, value) in layer) result[name] = value;
        return result;
    }

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new List<KeyValuePair<string, string>>();
        int position = 0, lineNumber = 0;
        (string text, string ending) ReadLine()
        {
            int start = position;
            while (position < text.Length && text[position] is not ('\r' or '\n')) position++;
            string line = text[start..position];
            int end = position;
            if (position < text.Length && text[position++] == '\r' && position < text.Length && text[position] == '\n') position++;
            lineNumber++;
            return (line, text[end..position]);
        }
        while (position < text.Length)
        {
            var (line, ending) = ReadLine();
            if (line.Length == 0) continue;
            int assignmentLine = lineNumber;
            try
            {
                int equals = line.IndexOf('=');
                int heredoc = line.IndexOf("<<", StringComparison.Ordinal);
                string name, value;
                if (heredoc >= 0 && (equals < 0 || heredoc < equals))
                {
                    name = line[..heredoc];
                    string marker = line[(heredoc + 2)..];
                    if (marker.Length == 0 || marker.Any(char.IsWhiteSpace)) throw new FormatException("Heredoc marker is empty or contains whitespace.");
                    var body = new StringBuilder();
                    string previousEnding = "";
                    bool closed = false;
                    while (position < text.Length)
                    {
                        var next = ReadLine();
                        if (next.text == marker) { closed = true; break; }
                        body.Append(previousEnding).Append(next.text);
                        previousEnding = next.ending;
                    }
                    if (!closed) throw new FormatException("Unclosed environment heredoc.");
                    value = body.ToString();
                }
                else
                {
                    if (equals < 0) throw new FormatException("Expected NAME=value or NAME<<MARKER.");
                    name = line[..equals]; value = line[(equals + 1)..];
                }
                values.Add(new(name, value));
                Snapshot(values);
            }
            catch (FormatException error) { throw new FormatException($"Line {assignmentLine}: {error.Message}"); }
        }
        return Snapshot(values);
    }

    public static string Format(IReadOnlyDictionary<string, string> values)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in Snapshot(values))
        {
            if (!value.Contains('\n') && !value.Contains('\r')) text.Append(name).Append('=').Append(value).Append('\n');
            else
            {
                string marker = "END_ENV";
                var lines = value.Split(['\r', '\n']);
                int suffix = 0;
                while (lines.Contains(marker)) marker = "END_ENV_" + ++suffix;
                text.Append(name).Append("<<").Append(marker).Append('\n').Append(value)
                    .Append(value.EndsWith('\r') ? '\r' : '\n').Append(marker).Append('\n');
            }
        }
        return text.ToString();
    }

    public static string FormatEditor(IReadOnlyDictionary<string, string> values)
        => Format(values).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    public static IReadOnlyDictionary<string, string> ParseEditor(string text, IReadOnlyDictionary<string, string> original)
    {
        var values = new Dictionary<string, string>(Parse(text.Replace("\r\n", "\n")), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in original)
            if (values.TryGetValue(name, out string? edited) && edited == value.Replace("\r\n", "\n").Replace('\r', '\n'))
                values[name] = value;
        return Snapshot(values);
    }

    public static IReadOnlyDictionary<string, string> ReadJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Environment must be an object of string values.");
        var values = new List<KeyValuePair<string, string>>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw new FormatException("Environment values must be strings.");
            values.Add(new(property.Name, property.Value.GetString()!));
        }
        return Snapshot(values);
    }

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return ReadJson(document.RootElement.GetProperty("env"));
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> values)
    {
        var snapshot = Snapshot(values);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { env = snapshot }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
