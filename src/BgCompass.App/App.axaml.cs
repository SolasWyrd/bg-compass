using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BgCompass.App.Runtime;

namespace BgCompass.App;

public partial class App : Application
{
    public static CliOptions Cli { get; set; } = CliOptions.Default;
    Shell? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The headless renderer builds its own windows; it must not start the log tracker or the tray.
        if (Cli.RenderUi is null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                _shell = new Shell(this, desktop, Cli);
                _shell.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось запустить приложение", ex);
                desktop.Shutdown(1);
            }
            desktop.Exit += (_, _) => { _shell?.Dispose(); _shell = null; };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
