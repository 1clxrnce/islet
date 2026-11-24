using Microsoft.UI.Xaml;

namespace Islet;

public partial class App : Application
{
    public const string DisplayName = "islet";

    private TrayIcon? _tray;
    private TestWindow? _testWindow;
    private IslandWindow? _island;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        // A tray app lives with no window open; it ends only through Exit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            // A background widget logs and keeps running instead of vanishing on a UI glitch.
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;
    public MainViewModel? ViewModel { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Trace($"Starting islet {typeof(App).Assembly.GetName().Version}", fresh: true);

        // Each step is wrapped: one failure is logged and the rest still start.
        Log.Step("Settings loaded", AppSettings.Load);
        // The tray comes first: even if everything else fails, Settings and Exit stay reachable.
        Log.Step("Tray icon", () => _tray = new TrayIcon(this));
        Log.Step("Media and volume services", () =>
        {
            ViewModel = new MainViewModel();
            _ = InitializeMediaAsync(ViewModel);
        });
        Log.Step("Island window", () =>
        {
            if (ViewModel is null) return;
            _island = new IslandWindow(ViewModel);
            _island.ShowIsland();
#if DEBUG
            if (Environment.GetEnvironmentVariable("ISLET_DEMO") is not null) _island.ShowDemo();
#endif
        });
    }

    private static async Task InitializeMediaAsync(MainViewModel viewModel)
    {
        await viewModel.InitializeAsync();
        Log.Trace("Media sessions: listening");
    }

    // Placeholder for the Settings window (phase 6): for now it brings back the test window.
    public void OpenSettings() => ShowTestWindow();

    // Someone started islet again while it runs.
    internal void OnSecondLaunch()
    {
        Log.Trace("Second launch redirected here");
        OpenSettings();
    }

    private void ShowTestWindow()
    {
        if (ViewModel is null) return;
        if (_testWindow is null)
        {
            _testWindow = new TestWindow(ViewModel);
            _testWindow.Closed += (_, _) => _testWindow = null;
        }
        _testWindow.Activate();
    }

    public void Shutdown()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Trace("Exiting");
        Log.Step("Tray removed", () => _tray?.Dispose());
        Log.Step("Island closed", () => _island?.Close());
        _testWindow?.Close();
        Log.Step("Services stopped", () => ViewModel?.Dispose());
        Log.Step("Settings saved", AppSettings.Save);
        Exit();
    }
}
