using System.Windows;
using Application = System.Windows.Application;

namespace DynamicIslandMusic;

public partial class App : Application
{
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tray = new TrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
