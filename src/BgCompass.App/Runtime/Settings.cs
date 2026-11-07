using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgCompass.App.Runtime;

/// <summary>Overlay panel state. X and Y are the top-left corner as a fraction of the game rect, so they survive a resolution change.</summary>
public sealed class PanelState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool Collapsed { get; set; }
}

public sealed class AppSettings
{
    public string? LogFolder { get; set; }
    public double UserScale { get; set; } = 1.0;
    public bool ClickThrough { get; set; }
    public bool PanelsVisible { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public PanelState Overlay { get; set; } = new();
}

/// <summary>settings.json. A damaged file means defaults, never a crash.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string? _path;
    readonly object _gate = new();

    public SettingsStore(string? path)
    {
        _path = path;
        Value = Load();
    }

    public AppSettings Value { get; }

    /// <summary>Raised after every Save, on the calling thread.</summary>
    public event Action? Saved;

    AppSettings Load()
    {
        if (_path is null || !File.Exists(_path)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
            s.Overlay ??= new();
            s.UserScale = Math.Clamp(double.IsFinite(s.UserScale) ? s.UserScale : 1.0, 0.5, 2.0);
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"settings.json не прочитан, используются значения по умолчанию: {e.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        if (_path is not null)
            lock (_gate)
            {
                try
                {
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Json));
                    File.Move(tmp, _path, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"settings.json не записан: {e.Message}");
                }
            }
        Saved?.Invoke();
    }
}
