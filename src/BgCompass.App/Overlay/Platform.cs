using Avalonia.Controls;

namespace BgCompass.App.Overlay;

/// <summary>
/// The single place where the Windows-only code is chosen. Everywhere else the app is platform-neutral:
/// on other systems the primary screen stands in for the game window and hotkeys are reported as unavailable.
/// </summary>
public static class Platform
{
    public static IGameWindowProbe CreateProbe(Func<PxRect> screen) =>
        OperatingSystem.IsWindows() ? new WindowsGameProbe() : new FixedGameProbe(screen);

    public static IHotkeys CreateHotkeys() =>
        OperatingSystem.IsWindows() ? new WindowsHotkeys() : new NoHotkeys();

    /// <summary>No focus, no taskbar, optional click-through. A no-op outside Windows.</summary>
    public static void ApplyOverlayStyles(Window window, bool clickThrough)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is { } h) WindowStyles.Apply(h, clickThrough);
    }

    /// <summary>Cursor position in physical pixels; null where it cannot be read. Only reads it.</summary>
    public static (int X, int Y)? CursorPosition() =>
        OperatingSystem.IsWindows() && Native.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>Whether a click at the screen point falls through the window. Always true outside Windows.</summary>
    public static bool PassesClicks(Window window, int x, int y) =>
        !OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } h || WindowStyles.PassesClicks(h, x, y);

    public static void RaiseTopmost(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is { } h) WindowStyles.RaiseTopmost(h);
    }
}
