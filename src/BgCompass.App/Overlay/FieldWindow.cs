using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BgCompass.App.ViewModels;
using BgCompass.App.Views;

namespace BgCompass.App.Overlay;

/// <summary>
/// The layer drawn on the game table: a transparent window exactly over the game's client area that every click passes
/// through. Its content is laid out in field units (height 1080) and scaled to the window.
/// </summary>
public sealed class FieldWindow : Window
{
    readonly LayoutTransformControl _scaler;
    readonly FieldView _view;

    public FieldWindow(FieldVm vm)
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "BG Compass Field";
        _view = new FieldView { DataContext = vm };
        _scaler = new LayoutTransformControl { Child = _view, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        Content = _scaler;
    }

    /// <summary>Exactly over the game's client area.</summary>
    public void Follow(PxRect game)
    {
        var s = Math.Max(RenderScaling, 0.01);
        var (w, h) = (game.W / s, game.H / s);
        // Width and Height start as NaN, so compare with "not close" rather than "far".
        if (!(Math.Abs(Width - w) <= 0.01)) Width = w;
        if (!(Math.Abs(Height - h) <= 0.01)) Height = h;
        var target = new PixelPoint((int)Math.Round(game.X), (int)Math.Round(game.Y));
        if (Position != target) Position = target;
        _view.Width = GameField.Width(game);
        _view.Height = GameField.Height;
        var k = game.H / GameField.Height / s;
        _scaler.LayoutTransform = new ScaleTransform(k, k);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Platform.ApplyOverlayStyles(this, clickThrough: true);
    }
}
