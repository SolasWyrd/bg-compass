using System.Text;

namespace BgCompass.Core.Logs;

/// <param name="FileExists">log.config exists.</param>
/// <param name="PowerEnabled">The [Power] section has every required setting with the required value.</param>
/// <param name="Problems">Required settings that are missing or have a different value.</param>
public sealed record LogConfigStatus(bool FileExists, bool PowerEnabled, IReadOnlyList<string> Problems);

/// <param name="Changed">The file was modified. False when [Power] was already enabled.</param>
/// <param name="BackupPath">Where the previous file was copied; null when there was nothing to back up.</param>
/// <param name="RestartRequired">Hearthstone reads log.config at startup, so a running game must be restarted.</param>
public sealed record LogConfigResult(bool Changed, string? BackupPath, bool RestartRequired);

/// <summary>
/// Checks and, only when explicitly asked, enables the [Power] section of Hearthstone's log.config.
/// Nothing here runs automatically.
/// </summary>
public sealed class LogConfig(string? path = null)
{
    private static readonly (string Key, string Value)[] Required =
    [
        ("LogLevel", "1"), ("FilePrinting", "True"), ("ConsolePrinting", "False"), ("ScreenPrinting", "False"), ("Verbose", "True"),
    ];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blizzard", "Hearthstone", "log.config");

    public string FilePath { get; } = path ?? DefaultPath;

    public LogConfigStatus Check()
    {
        if (!File.Exists(FilePath)) return new LogConfigStatus(false, false, Required.Select(r => r.Key).ToArray());
        var lines = File.ReadAllText(FilePath).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var (start, end) = FindSection(lines);
        var problems = new List<string>();
        foreach (var (key, value) in Required)
        {
            var idx = start < 0 ? -1 : FindKey(lines, start, end, key);
            if (idx < 0 || !SameValue(lines[idx], value)) problems.Add(key);
        }
        return new LogConfigStatus(true, problems.Count == 0, problems);
    }

    /// <summary>
    /// Makes sure [Power] has the required settings. Other sections and lines are kept as they are; the previous file is
    /// copied to a .bak file first. Does nothing when already enabled.
    /// </summary>
    public LogConfigResult EnablePower()
    {
        var status = Check();
        if (status.PowerEnabled) return new LogConfigResult(false, null, false);

        string? backup = null;
        var newline = "\r\n";
        var lines = new List<string>();
        if (File.Exists(FilePath))
        {
            backup = FilePath + ".bak";
            if (File.Exists(backup)) backup = $"{FilePath}.{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Copy(FilePath, backup);
            var text = File.ReadAllText(FilePath);
            if (!text.Contains("\r\n") && text.Contains('\n')) newline = "\n";
            lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        }

        var (start, end) = FindSection(lines);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[Power]");
            foreach (var (key, value) in Required) lines.Add($"{key}={value}");
        }
        else
        {
            foreach (var (key, value) in Required)
            {
                var idx = FindKey(lines, start, end, key);
                if (idx >= 0) lines[idx] = $"{key}={value}";
                else { lines.Insert(end, $"{key}={value}"); end++; }
            }
        }

        File.WriteAllText(FilePath, string.Join(newline, lines) + newline, new UTF8Encoding(false));
        return new LogConfigResult(true, backup, true);
    }

    /// <summary>Line range [start, end) of the [Power] section's body; start is -1 when there is no such section.</summary>
    private static (int Start, int End) FindSection(List<string> lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('['))
            {
                if (start >= 0) return (start, TrimTrailingBlank(lines, start, i));
                if (t.Equals("[Power]", StringComparison.OrdinalIgnoreCase)) start = i + 1;
            }
        }
        return start < 0 ? (-1, -1) : (start, TrimTrailingBlank(lines, start, lines.Count));
    }

    // New keys go right after the last real line of the section, not after the blank line that separates sections.
    private static int TrimTrailingBlank(List<string> lines, int start, int end)
    {
        while (end > start && lines[end - 1].Trim().Length == 0) end--;
        return end;
    }

    private static int FindKey(List<string> lines, int start, int end, string key)
    {
        for (int i = start; i < end; i++)
        {
            var t = lines[i].TrimStart();
            var eq = t.IndexOf('=');
            if (eq > 0 && t.AsSpan(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static bool SameValue(string line, string expected)
    {
        var v = line[(line.IndexOf('=') + 1)..].Trim();
        return v.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
