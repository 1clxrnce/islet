using Microsoft.UI.Xaml.Controls;

namespace Islet;

// Notification-area icon through WinUIEx: a WinUI MenuFlyout as its menu, so it follows the Windows theme.
internal sealed class TrayIcon : IDisposable
{
    private const uint TrayId = 1;
    private readonly WinUIEx.TrayIcon _icon;
    private readonly App _app;

    public TrayIcon(App app)
    {
        _app = app;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "islet.ico");
        _icon = new WinUIEx.TrayIcon(TrayId, iconPath, App.DisplayName);
        _icon.Selected += (_, _) => _app.OpenSettings();
        _icon.ContextMenu += (_, e) => e.Flyout = BuildMenu();
        _icon.IsVisible = true;
    }

    // Built on each open, so later phases can show the current hide state.
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Settings", "", _app.OpenSettings));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Exit", "", _app.Shutdown));
        return menu;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose() => _icon.Dispose();
}
