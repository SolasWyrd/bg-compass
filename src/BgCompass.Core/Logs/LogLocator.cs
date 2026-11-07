using System.Runtime.Versioning;

namespace BgCompass.Core.Logs;

/// <summary>
/// Finds Hearthstone's Power.log. Reads the Uninstall registry key and the file system only; it never opens or
/// inspects the game process.
/// </summary>
public static class LogLocator
{
    private const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone";

    /// <summary>
    /// Returns the newest Power.log: the user override first (an install folder, a Logs folder or the file itself),
    /// then the registry install location, then the common default folders. Null when none exists.
    /// </summary>
    public static string? FindPowerLog(string? userOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            var fromOverride = FromPath(userOverride);
            if (fromOverride is not null) return fromOverride;
        }
        foreach (var dir in InstallCandidates())
        {
            var found = NewestPowerLog(Path.Combine(dir, "Logs"));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Install folders to try: the registry value, then defaults. Existence is not checked here.</summary>
    public static IEnumerable<string> InstallCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registry = ReadRegistryInstallLocation();
        if (registry is not null && seen.Add(registry)) yield return registry;

        foreach (var d in DefaultInstallFolders())
            if (seen.Add(d)) yield return d;
    }

    private static IEnumerable<string> DefaultInstallFolders()
    {
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
            if (pf.Length > 0) yield return Path.Combine(pf, "Hearthstone");

        if (!OperatingSystem.IsWindows()) yield break;
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { yield break; }
        foreach (var d in drives)
        {
            if (d.DriveType != DriveType.Fixed) continue;
            yield return Path.Combine(d.Name, "Hearthstone");
            yield return Path.Combine(d.Name, "Games", "Hearthstone");
            yield return Path.Combine(d.Name, "Program Files (x86)", "Hearthstone");
        }
    }

    private static string? ReadRegistryInstallLocation()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ReadRegistryWindows();
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string is { Length: > 0 } s ? s : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? FromPath(string path)
    {
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
            return NewestPowerLog(Path.Combine(path, "Logs")) ?? NewestPowerLog(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The most recently written of <c>Hearthstone_*/Power.log</c> under <paramref name="logsDir"/> and the legacy
    /// <c>Power.log</c> directly inside it. Null when there is none.
    /// </summary>
    public static string? NewestPowerLog(string logsDir)
    {
        try
        {
            if (!Directory.Exists(logsDir)) return null;
            string? best = null;
            var bestTime = DateTime.MinValue;
            void Consider(string file)
            {
                if (!File.Exists(file)) return;
                var t = File.GetLastWriteTimeUtc(file);
                if (best is null || t > bestTime) { best = file; bestTime = t; }
            }
            Consider(Path.Combine(logsDir, "Power.log"));
            foreach (var session in Directory.EnumerateDirectories(logsDir, "Hearthstone_*"))
                Consider(Path.Combine(session, "Power.log"));
            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
