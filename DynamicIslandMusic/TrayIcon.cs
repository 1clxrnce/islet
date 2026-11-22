using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DynamicIslandMusic;

internal sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DynamicIslandMusic";

    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _startWithWindows;

    public TrayIcon()
    {
        _startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        _startWithWindows.CheckedChanged += (_, _) => SetStartupEnabled(_startWithWindows.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => (System.Windows.Application.Current.MainWindow as MainWindow)?.OpenOptions());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _notify = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Dynamic Island Music",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += (_, _) => ShowWindow();
    }

    private static void ShowWindow()
    {
        var window = System.Windows.Application.Current.MainWindow;
        if (window is null) return;
        window.Show();
        window.WindowState = System.Windows.WindowState.Normal;
    }

    internal static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string value && value.Length > 0;
    }

    internal static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(20, 20, 22));
            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 9);
            g.FillPath(background, path);

            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, 8, 18, 7, 6);
            g.FillRectangle(white, 14, 8, 2, 11);
            g.FillEllipse(white, 18, 17, 7, 6);
            g.FillRectangle(white, 24, 7, 2, 11);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
        path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
        path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
