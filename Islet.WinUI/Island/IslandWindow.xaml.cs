using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinUIEx;
using Color = Windows.UI.Color;

namespace Islet;

// The island: a transparent, topmost, never-focused window that draws only the pill.
public sealed partial class IslandWindow : WindowEx
{
    private static readonly Windows.Foundation.Size CompactBaseSize = new(260, 44);
    private const double PillTop = 8;        // Gap above the pill inside the window, in DIPs
    private const double ShadowMargin = 24;  // Room around the pill
    private const double EdgeMargin = 12;    // Gap from the screen edge in the corner positions

    private readonly MainViewModel _vm;
    private readonly IntPtr _hwnd;
    private readonly Marquee _marquee;
    private readonly Equalizer _equalizer;
    private readonly Compositor _compositor;
    private readonly DispatcherQueueTimer _syncTimer;   // Slow safety resync of the media timeline
    private readonly UISettings _uiSettings = new();
    private readonly WinUIEx.Messaging.WindowMessageMonitor _messages;
    private Vector3KeyFrameAnimation? _progressRun;
    private bool _clickThrough;
    private bool _queuedVisuals;
    private bool _queuedMarquee;
    private bool _closed;
    private double _scale = 1;     // Physical pixels per DIP on the island's monitor
    private PointInt32 _origin;    // Window position, physical pixels

    public IslandWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _hwnd = this.GetWindowHandle();
        _compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;

        SystemBackdrop = new TransparentTintBackdrop(); // Fully transparent: only the pill shows.
        // Before ConfigureWindow, so the frame recalculation it triggers already sees our handler.
        _messages = new WinUIEx.Messaging.WindowMessageMonitor(this);
        _messages.WindowMessageReceived += OnWindowMessage;
        ConfigureWindow();

        _marquee = new Marquee(TitleText, TitleViewport, TitleSourceClip, TitleSource, FadeHost);
        _equalizer = new Equalizer(EqualizerBars);
        _equalizer.SetFill(_vm.AccentBrush);
        ProgressFill.Fill = _vm.AccentBrush;
        ProgressFill.CenterPoint = Vector3.Zero;

        _syncTimer = DispatcherQueue.CreateTimer();
        _syncTimer.Interval = TimeSpan.FromSeconds(2);
        _syncTimer.Tick += (_, _) => _vm.SyncTimeline();

        _vm.PropertyChanged += OnViewModelChanged;
        MouseHook.PillHoverChanged += OnPillHover;
        _uiSettings.ColorValuesChanged += OnWindowsColorsChanged;
        TitleViewport.SizeChanged += (_, _) => QueueMarquee();
        Closed += (_, _) => Shutdown();
    }

    private static AppSettings Settings => AppSettings.Current;

    private static Windows.Foundation.Size PillSize => new(
        CompactBaseSize.Width * Settings.PillScale,
        CompactBaseSize.Height * Settings.PillScale);

    // Shows the island without taking focus from whatever the user is doing.
    public void ShowIsland()
    {
        Log.Step("Island look", ApplyLook);
        Log.Step("Island position", PositionWindow);
        Log.Step("Island content", RefreshAll);
        Log.Step("Island shown", () =>
        {
            AppWindow.Show(activateWindow: false);
            Native.SetWindowPos(_hwnd, Native.HwndTopmost, 0, 0, 0, 0,
                Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate | Native.SwpShowWindow);
        });
        Log.Step("Mouse hook", MouseHook.Start);
        Log.Step("Click-through", () => SetClickThrough(true));
    }

    #region Window setup

    private void ConfigureWindow()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // Not in the taskbar or Alt+Tab.

        // A plain popup: the presenter leaves a dialog frame (WS_DLGFRAME) that draws a 1 px edge.
        long style = Native.GetWindowLongPtr(_hwnd, Native.GwlStyle).ToInt64();
        style = (style & ~Native.FrameStyles) | Native.WsPopup;
        Native.SetWindowLongPtr(_hwnd, Native.GwlStyle, new IntPtr(style));

        // Tool window: no taskbar button. No-activate: clicks never steal focus from the app in front.
        // Layered (fully opaque): lets WS_EX_TRANSPARENT pass clicks through to other apps.
        long ex = Native.GetExStyle(_hwnd);
        ex = (ex | Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExLayered) & ~Native.WsExAppWindow;
        Native.SetExStyle(_hwnd, ex);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LwaAlpha);

        // No Windows 11 frame: no rounded window corners, no border; the pill draws its own shape.
        int corner = Native.DwmwcpDoNotRound;
        Native.DwmSetWindowAttribute(_hwnd, Native.DwmwaWindowCornerPreference, ref corner, sizeof(int));
        uint border = Native.DwmwaColorNone;
        Native.DwmSetWindowAttribute(_hwnd, Native.DwmwaBorderColor, ref border, sizeof(uint));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpFrameChanged);
        Log.Trace($"Window styles 0x{Native.GetWindowLongPtr(_hwnd, Native.GwlStyle).ToInt64():X8}, ex 0x{Native.GetExStyle(_hwnd):X8}");
    }

    // WS_EX_TRANSPARENT: every click and scroll goes to the app behind. On everywhere except over the pill.
    private void SetClickThrough(bool enabled)
    {
        if (_closed || _clickThrough == enabled) return;
        long style = Native.GetExStyle(_hwnd);
        long next = enabled ? style | Native.WsExTransparent : style & ~Native.WsExTransparent;
        if (next != style) Native.SetExStyle(_hwnd, next);
        _clickThrough = enabled;
    }

    private void OnPillHover(bool over) => SetClickThrough(!over);

    #endregion

    #region Placement and DPI

    private static DisplayArea TargetDisplay()
    {
        var all = DisplayArea.FindAll();
        int index = Settings.MonitorIndex;
        if (index >= 0 && index < all.Count) return all[index];
        return DisplayArea.Primary;
    }

    private static double ScaleFor(DisplayArea area)
    {
        try
        {
            IntPtr monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Monitor DPI");
        }
        return 1;
    }

    // Moves and sizes the window in one call, in physical pixels, from the monitor's own DPI.
    public void PositionWindow()
    {
        if (_closed) return;
        var display = TargetDisplay();
        var area = display.OuterBounds; // The whole monitor: the island sits on its top edge.
        _scale = ScaleFor(display);

        var pill = PillSize;
        Pill.Width = pill.Width;
        Pill.Height = pill.Height;
        double radius = pill.Height / 2;
        Pill.CornerRadius = Tint.CornerRadius = Blur.CornerRadius = new CornerRadius(radius);
        Pill.HorizontalAlignment = Settings.Position switch
        {
            IslandPosition.TopLeft => HorizontalAlignment.Left,
            IslandPosition.TopRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };

        int w = (int)Math.Ceiling((pill.Width + 2 * ShadowMargin) * _scale);
        int h = (int)Math.Ceiling((PillTop + pill.Height + ShadowMargin) * _scale);
        int margin = (int)Math.Round(ShadowMargin * _scale);
        int edge = (int)Math.Round(EdgeMargin * _scale);
        int x = Settings.Position switch
        {
            IslandPosition.TopLeft => area.X + edge - margin,
            IslandPosition.TopRight => area.X + area.Width - edge + margin - w,
            _ => area.X + (area.Width - w) / 2
        } + (int)Math.Round(Settings.OffsetX * _scale);
        int y = area.Y + (int)Math.Round(Settings.OffsetY * _scale);

        var bounds = new RectInt32(x, y, w, h);
        if (bounds.Equals(_bounds)) return; // Windows sends setting changes often; most move nothing.
        _bounds = bounds;
        _origin = new PointInt32(x, y);
        AppWindow.MoveAndResize(bounds);
        UpdateHookShape(w);
        QueueMarquee();
        Log.Trace($"Placed on display {display.DisplayId.Value} at {x},{y} {w}x{h} px, scale {_scale:0.##}");
    }

    private RectInt32 _bounds;

    // The pill in screen pixels, for the mouse hook (hover and click-through).
    private void UpdateHookShape(int windowWidthPx)
    {
        var pill = PillSize;
        double pillW = pill.Width * _scale, pillH = pill.Height * _scale, margin = ShadowMargin * _scale;
        double left = Pill.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => margin,
            HorizontalAlignment.Right => windowWidthPx - margin - pillW,
            _ => (windowWidthPx - pillW) / 2
        };
        MouseHook.Pill = _closed ? null : new ScreenShape(
            _origin.X + left, _origin.Y + PillTop * _scale, pillW, pillH, pillH / 2);
    }

    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmSettingChange = 0x001A; // Taskbar or work area changes

    private const uint WmStyleChanging = 0x007C;
    private const uint WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcPaint = 0x0085;
    private const uint WmNcActivate = 0x0086;
    private const uint WmNcUahDrawCaption = 0x00AE; // Undocumented: themed caption painting
    private const uint WmNcUahDrawFrame = 0x00AF;   // Undocumented: themed frame painting

    // The island must never show a window frame. The presenter keeps a dialog frame style that
    // Windows repaints (as a small title bar) whenever a style changes, e.g. when click-through
    // toggles. So: no frame area, no frame painting, and frame styles stripped whenever set.
    private void OnWindowMessage(object? sender, WinUIEx.Messaging.WindowMessageEventArgs e)
    {
        uint id = e.Message.MessageId;
        if (id == WmNcCalcSize && e.Message.WParam != 0)
        {
            // The whole window is client area: Windows has no frame left to draw.
            e.Result = 0;
            e.Handled = true;
        }
        else if (id is WmNcPaint or WmNcUahDrawCaption or WmNcUahDrawFrame)
        {
            e.Result = 0;
            e.Handled = true;
        }
        else if (id == WmMouseActivate)
        {
            e.Result = MaNoActivate; // A click on the pill must never take focus from the app in front.
            e.Handled = true;
        }
        else if (id == WmNcActivate)
        {
            e.Result = 1; // "Done" without letting Windows draw an active or inactive caption.
            e.Handled = true;
        }
        else if (id == WmStyleChanging && (int)e.Message.WParam == Native.GwlStyle && e.Message.LParam != 0)
        {
            // STYLESTRUCT { styleOld, styleNew }: drop the frame from whatever style is being set.
            IntPtr change = (IntPtr)e.Message.LParam;
            uint styleNew = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(change, 4);
            uint stripped = (uint)((styleNew & ~Native.FrameStyles) | Native.WsPopup);
            if (stripped != styleNew) System.Runtime.InteropServices.Marshal.WriteInt32(change, 4, unchecked((int)stripped));
        }
        // Monitors added, removed, rearranged or rescaled: place the island again.
        else if (id is WmDisplayChange or WmDpiChanged or WmSettingChange)
        {
            DispatcherQueue.TryEnqueue(PositionWindow);
        }
    }

    #endregion

    #region Look (full themes come with the Settings window)

    private void OnWindowsColorsChanged(UISettings sender, object args) => Ui.Post(ApplyLook);

    public void ApplyLook()
    {
        if (_closed) return;
        bool light = Settings.Theme switch
        {
            ThemeMode.Light => true,
            ThemeMode.System => IsWindowsLight(),
            _ => false
        };
        bool black = Settings.Theme == ThemeMode.Black;
        Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;

        Color body = Settings.Theme switch
        {
            ThemeMode.Black => Colors.Black,
            ThemeMode.AlbumArt => Mix(_vm.AccentColor, Colors.Black, 0.8),
            _ => light ? ColorHelper.FromArgb(255, 0xF3, 0xF3, 0xF3) : ColorHelper.FromArgb(255, 0x20, 0x20, 0x20)
        };
        // Fluent flyouts are mostly opaque; "more glass" lets more of the blur through.
        byte alpha = black ? (byte)255 : (byte)(255 * (1 - 0.3 * Settings.BlurStrength));
        Tint.Background = new SolidColorBrush(ColorHelper.FromArgb(alpha, body.R, body.G, body.B));
        Tint.BorderBrush = new SolidColorBrush(black ? ColorHelper.FromArgb(255, 0x26, 0x26, 0x26)
            : light ? ColorHelper.FromArgb(0x24, 0, 0, 0) : ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

        // Black is solid: no glass at all. Otherwise real acrylic behind the pill.
        if (black) Blur.SystemBackdrop = null;
        else if (Blur.SystemBackdrop is null) Blur.SystemBackdrop = new DesktopAcrylicBackdrop();
        Blur.Visibility = black ? Visibility.Collapsed : Visibility.Visible;

        _vm.AccentBrush.Color = Accent();
    }

    private Color Accent() => Settings.Accent switch
    {
        AccentSource.AlbumArt => _vm.AccentColor,
        AccentSource.Custom => ParseColor(Settings.AccentColor) ?? SystemAccent(),
        _ => SystemAccent()
    };

    private Color SystemAccent()
    {
        try { return _uiSettings.GetColorValue(UIColorType.Accent); }
        catch (Exception) { return ColorHelper.FromArgb(255, 0x00, 0x78, 0xD4); }
    }

    private bool IsWindowsLight()
    {
        try { return _uiSettings.GetColorValue(UIColorType.Background).R > 128; }
        catch (Exception) { return false; }
    }

    private static Color? ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return null;
    }

    private static Color Mix(Color a, Color b, double t) => ColorHelper.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    #endregion

    #region Content

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AccentColor):
                if (Settings.Accent == AccentSource.AlbumArt || Settings.Theme == ThemeMode.AlbumArt) ApplyLook();
                break;
            case nameof(MainViewModel.DisplayTitle):
            case nameof(MainViewModel.TrackRevision):
                TitleText.Text = _vm.DisplayTitle;
                QueueMarquee();
                break;
            case nameof(MainViewModel.Volume):
            case nameof(MainViewModel.IsMuted):
            case nameof(MainViewModel.VolumeGlyph):
                break; // Not shown on the pill.
            case nameof(MainViewModel.Position):
            case nameof(MainViewModel.Progress):
            case nameof(MainViewModel.Elapsed):
            case nameof(MainViewModel.Remaining):
                // The progress line runs on its own animation. It is re-aimed on each Windows update
                // (which also raises Duration); reacting here would loop, as re-aiming moves Position.
                break;
            default:
                QueueVisuals(); // Many properties change together; update once after the batch.
                break;
        }
    }

    private void RefreshAll()
    {
        TitleText.Text = _vm.DisplayTitle;
        RefreshVisuals();
        QueueMarquee();
    }

    private void QueueVisuals()
    {
        if (_queuedVisuals) return;
        _queuedVisuals = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _queuedVisuals = false;
            RefreshVisuals();
        });
    }

    private void RefreshVisuals()
    {
        if (_closed) return;
        ArtBrush.ImageSource = _vm.AlbumArt;
        ArtPlaceholder.Visibility = _vm.AlbumArt is null ? Visibility.Visible : Visibility.Collapsed;
        EqualizerBars.Visibility = _vm.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _equalizer.Update(_vm.IsPlaying, paused: false);
        ProgressTrack.Visibility = _vm.IsActive && !_vm.IsLive && Settings.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress();
        if (_vm.IsActive) _syncTimer.Start(); else _syncTimer.Stop();
    }

    // The fill is scaled from where the song is now to the end, over the time that is left, on the
    // composition thread. Re-aimed whenever Windows reports a new position, so no timer drives it.
    private void UpdateProgress()
    {
        _vm.Tick(); // Bring the position up to now.
        float progress = (float)Math.Clamp(_vm.Progress, 0, 1);
        if (_progressRun is not null)
        {
            ProgressFill.StopAnimation(_progressRun);
            _progressRun = null;
        }
        ProgressFill.Scale = new Vector3(progress, 1, 1);

        var remaining = _vm.Duration - _vm.Position;
        if (!_vm.IsPlaying || _vm.IsLive || _vm.Duration <= TimeSpan.Zero || remaining <= TimeSpan.Zero) return;

        var run = _compositor.CreateVector3KeyFrameAnimation();
        var linear = _compositor.CreateLinearEasingFunction();
        run.Target = "Scale";
        run.InsertKeyFrame(0f, new Vector3(progress, 1, 1), linear);
        run.InsertKeyFrame(1f, Vector3.One, linear);
        run.Duration = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, (long)(remaining.Ticks / _vm.PlaybackRate)));
        ProgressFill.StartAnimation(run);
        _progressRun = run;
    }

    private void QueueMarquee()
    {
        if (_queuedMarquee) return;
        _queuedMarquee = true;
        // After layout, so the viewport and title have their final widths.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _queuedMarquee = false;
            if (!_closed) _marquee.Update(paused: false);
        });
    }

    #endregion

#if DEBUG
    // Debug builds only: ISLET_DEMO=1 fakes a playing song, so the marquee, equalizer and progress
    // line can be checked without music.
    public void ShowDemo()
    {
        TitleText.Text = "A deliberately long song title that cannot fit in the pill - Demo Artist";
        QueueMarquee();
        EqualizerBars.Visibility = Visibility.Visible;
        _equalizer.Update(playing: true, paused: false);
        ProgressTrack.Visibility = Visibility.Visible;
        ProgressFill.Scale = new Vector3(0.4f, 1, 1);
    }
#endif

    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _syncTimer.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        MouseHook.PillHoverChanged -= OnPillHover;
        _uiSettings.ColorValuesChanged -= OnWindowsColorsChanged;
        _messages.WindowMessageReceived -= OnWindowMessage;
        _messages.Dispose();
        MouseHook.Stop();
        _marquee.Stop();
    }
}
