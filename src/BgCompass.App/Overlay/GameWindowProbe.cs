namespace BgCompass.App.Overlay;

/// <param name="Found">The Hearthstone window exists and is not minimised.</param>
/// <param name="Rect">Its client area on the virtual screen, in physical pixels.</param>
/// <param name="ShouldShow">Hearthstone or one of our own windows is the foreground window.</param>
public readonly record struct GameWindowInfo(bool Found, PxRect Rect, bool ShouldShow);

/// <summary>Where the game is. Implementations only read window geometry; they never touch the game process.</summary>
public interface IGameWindowProbe
{
    GameWindowInfo Poll();
}

/// <summary>A fixed rectangle: the primary screen on systems without Hearthstone, and render/test runs.</summary>
public sealed class FixedGameProbe(Func<PxRect> rect) : IGameWindowProbe
{
    public GameWindowInfo Poll() => new(true, rect(), true);
}

public enum HotkeyAction { TogglePanels, ToggleClickThrough }

public sealed record HotkeyStatus(string Name, string Keys, bool Registered, string Detail);

/// <summary>Global hotkeys through RegisterHotKey (never a keyboard hook).</summary>
public interface IHotkeys : IDisposable
{
    IReadOnlyList<HotkeyStatus> Status { get; }
    event Action<HotkeyAction>? Pressed;
    void Start();
}

public sealed class NoHotkeys : IHotkeys
{
    public IReadOnlyList<HotkeyStatus> Status { get; } =
    [
        new("Показать или скрыть панели", "Ctrl+Shift+H", false, "Горячие клавиши работают только в Windows."),
        new("Сквозной режим", "Ctrl+Shift+T", false, "Горячие клавиши работают только в Windows."),
    ];
    public event Action<HotkeyAction>? Pressed { add { } remove { } }
    public void Start() { }
    public void Dispose() { }
}
