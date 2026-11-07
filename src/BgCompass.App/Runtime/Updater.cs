using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BgCompass.App.Runtime;

/// <summary>The newest release as far as the updater cares: its version and the two files it needs.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri ExeUrl, Uri HashUrl);

/// <summary>Pure, network-free parts of the updater: version compare, release JSON parsing, hash file parsing.</summary>
public static class UpdateLogic
{
    public const string ExeName = "BgCompass.exe";
    public const string HashName = "BgCompass.exe.sha256";

    /// <summary>"v0.2.0" or "0.2" to a version; null when malformed.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        return Version.TryParse(s, out var v) ? v : null;
    }

    static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>Picks the release when it is newer than <paramref name="current"/> and carries both assets over HTTPS; otherwise null with a reason.</summary>
    public static ReleaseInfo? SelectRelease(string json, Version current, out string reason)
    {
        reason = "";
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { reason = "ответ не JSON"; return null; }
        if (root.ValueKind != JsonValueKind.Object) { reason = "неожиданный ответ"; return null; }

        if (Flag(root, "draft") || Flag(root, "prerelease")) { reason = "черновик или предварительный выпуск"; return null; }
        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var version = ParseTag(tag);
        if (version is null) { reason = $"тег '{tag}' не версия"; return null; }
        if (!IsNewer(version, current)) { reason = "новее нет"; return null; }

        Uri? exe = null, hash = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                if (name == ExeName) exe = uri;
                else if (name == HashName) hash = uri;
            }
        if (exe is null || hash is null) { reason = $"в выпуске {tag} нет {ExeName} или {HashName}"; return null; }
        return new ReleaseInfo(version, tag!, exe, hash);
    }

    /// <summary>Same arguments as this run, minus an earlier --wait-pid, plus one for this process.</summary>
    public static IReadOnlyList<string> RestartArgs(IEnumerable<string> args, int pid)
    {
        var kept = new List<string>();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == "--wait-pid") { i++; continue; }
            kept.Add(list[i]);
        }
        kept.Add("--wait-pid");
        kept.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return kept;
    }

    /// <summary>The first token of a .sha256 file, lowercased; null unless it is 64 hex digits.</summary>
    public static string? ParseHash(string text)
    {
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : null;
    }
}

/// <summary>
/// Self-update from GitHub Releases. Downloads and swaps the exe quietly; the new version starts only when the user
/// restarts (<see cref="Restart"/>). Every failure is one log line and a retry on the next tick.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string LatestUrl = "https://api.github.com/repos/SolasWyrd/bg-compass/releases/latest";
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    readonly string _exePath;
    readonly Version _current;
    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Action<string?> _onReady;
    readonly Action _shutdown;
    readonly CancellationTokenSource _cts = new();
    readonly Timer _timer;
    int _busy;
    string? _ready;

    public Updater(string exePath, Version current, Action<string?> onReady, Action shutdown, HttpMessageHandler? handler = null)
    {
        _exePath = exePath; _current = current; _onReady = onReady; _shutdown = shutdown;
        _ownsHttp = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BgCompass");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _timer = new Timer(_ => _ = CheckOnceAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True for the published exe on Windows, false for dotnet run, tests, other systems.</summary>
    public static bool IsPublishedExe(string? processPath) =>
        OperatingSystem.IsWindows() && string.Equals(Path.GetFileName(processPath), UpdateLogic.ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when updating does not apply to this run (no timers, no leftovers touched).</summary>
    public static Updater? TryCreate(Action<string?> onReady, Action shutdown)
    {
        var path = Environment.ProcessPath;
        if (!IsPublishedExe(path)) return null;
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? null : new Updater(path!, version, onReady, shutdown);
    }

    public void Start()
    {
        CleanLeftovers(_exePath);
        _timer.Change(FirstDelay, Interval);
    }

    public static void CleanLeftovers(string exePath)
    {
        foreach (var f in new[] { exePath + ".old", exePath + ".new" })
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One check-download-swap cycle. Never throws; returns the version that is now ready, if any.</summary>
    public async Task<string?> CheckOnceAsync()
    {
        if (_ready is not null || Interlocked.Exchange(ref _busy, 1) == 1) return _ready;
        try
        {
            var ct = _cts.Token;
            var json = await _http.GetStringAsync(LatestUrl, ct);
            var release = UpdateLogic.SelectRelease(json, _current, out var reason);
            if (release is null)
            {
                if (reason.StartsWith("в выпуске", StringComparison.Ordinal)) AppLog.Warn("Обновление: " + reason);
                return null;
            }
            var expected = UpdateLogic.ParseHash(await _http.GetStringAsync(release.HashUrl, ct));
            if (expected is null) { AppLog.Warn("Обновление: файл .sha256 не разобран"); return null; }
            if (!await DownloadVerifiedAsync(release.ExeUrl, _exePath + ".new", expected, ct)) return null;
            Swap(_exePath);
            _ready = release.Version.ToString(3);
            AppLog.Info($"Обновление {_ready} установлено, вступит в силу после перезапуска");
            _onReady(_ready);
            return _ready;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Warn($"Обновление: {e.Message}");
            return null;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Streams the file to <paramref name="target"/>; on a hash mismatch or any failure the file is deleted.</summary>
    public async Task<bool> DownloadVerifiedAsync(Uri url, string target, string expectedHash, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            string actual;
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
                actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (actual == expectedHash) return true;
            AppLog.Warn("Обновление: контрольная сумма не совпала, файл удалён");
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        TryDelete(target);
        return false;
    }

    /// <summary>exe to exe.old, exe.new to exe; if the second step fails the first is undone.</summary>
    public static void Swap(string exePath)
    {
        var old = exePath + ".old";
        var fresh = exePath + ".new";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try { File.Move(fresh, exePath); }
        catch
        {
            File.Move(old, exePath, true);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Starts the new exe with the same arguments and shuts this instance down. Call only when an update is ready.</summary>
    public void Restart()
    {
        if (_ready is null) return;
        try
        {
            var psi = new ProcessStartInfo(_exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exePath)! };
            foreach (var a in UpdateLogic.RestartArgs(Environment.GetCommandLineArgs().Skip(1), Environment.ProcessId)) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Не удалось перезапустить", e);
            return;
        }
        _shutdown();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
