using Avalonia.Controls;

namespace BgCompass.App;

/// <summary>Closing the window hides it to the tray; only <see cref="Quit"/> really closes it.</summary>
public partial class MainWindow : Window
{
    bool _quitting;

    public MainWindow() => InitializeComponent();

    public void Quit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
