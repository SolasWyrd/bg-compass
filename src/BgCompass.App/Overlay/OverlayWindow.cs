using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BgCompass.App.Runtime;
using BgCompass.App.ViewModels;
using BgCompass.App.Views;

namespace BgCompass.App.Overlay;

/// <summary>
/// The panel's native window: borderless, transparent, topmost, never activated, not in the taskbar.
/// Win32 specifics (no-activate, tool window, click-through) are applied in <see cref="Platform"/>.
/// Sizes: the panel content is laid out in "design pixels" (1080p, scale 1) and scaled with a layout transform of
/// <c>physicalScale / RenderScaling</c>, so one design pixel is <c>physicalScale</c> physical pixels on every monitor.
/// </summary>
public sealed class OverlayWindow : Window
{
    readonly LayoutTransformControl _scaler;
    readonly Border _root;
    PixelPoint _dragStartScreen, _dragStartPos;
    bool _dragging;

    public OverlayWindow(CompassVm vm)
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        Title = "BG Compass";
        ToolTip.SetShowDelay(this, 150);

        _root = new Border { Width = PanelLayout.DesignWidth, MaxHeight = PanelLayout.DesignMaxHeight, Child = new CompassFrame { DataContext = vm }, VerticalAlignment = VerticalAlignment.Top };
        _scaler = new LayoutTransformControl { Child = _root };
        Content = _scaler;

        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble);
    }

    public bool ClickThrough { get; private set; }
    public double Scale { get; private set; } = 1;
    public bool IsDragging => _dragging;

    /// <summary>Raised after a drag with the new top-left in physical pixels.</summary>
    public event Action<int, int>? Dragged;

    /// <summary>Physical size of the window right now.</summary>
    public (double W, double H) PhysicalSize => (Bounds.Width * RenderScaling, Bounds.Height * RenderScaling);

    /// <param name="physicalScale">Physical pixels per design pixel.</param>
    public void SetScale(double physicalScale)
    {
        Scale = physicalScale;
        var k = physicalScale / Math.Max(RenderScaling, 0.01);
        _scaler.LayoutTransform = new ScaleTransform(k, k);
    }

    public void SetClickThrough(bool on)
    {
        ClickThrough = on;
        Platform.ApplyOverlayStyles(this, ClickThrough);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Platform.ApplyOverlayStyles(this, ClickThrough);
    }

    void OnPressed(object? s, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ClickThrough || !OnDragHandle(e.Source)) return;
        _dragStartScreen = this.PointToScreen(e.GetPosition(this));
        _dragStartPos = Position;
        _dragging = true;
        e.Pointer.Capture(this);
    }

    bool OnDragHandle(object? source)
    {
        var header = (_root.Child as CompassFrame)?.FindControl<Control>("Header");
        return header is not null && source is Visual v && (ReferenceEquals(v, header) || header.IsVisualAncestorOf(v));
    }

    void OnMoved(object? s, PointerEventArgs e)
    {
        if (!_dragging) return;
        var now = this.PointToScreen(e.GetPosition(this));
        Position = new PixelPoint(_dragStartPos.X + now.X - _dragStartScreen.X, _dragStartPos.Y + now.Y - _dragStartScreen.Y);
    }

    void OnReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (Position != _dragStartPos) Dragged?.Invoke(Position.X, Position.Y);
    }
}
