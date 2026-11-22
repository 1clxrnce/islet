using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Screen = System.Windows.Forms.Screen;
using SystemEvents = Microsoft.Win32.SystemEvents;
using WindowInteropHelper = System.Windows.Interop.WindowInteropHelper;

namespace DynamicIslandMusic;

public partial class MainWindow : Window
{
    private enum IslandState { Hidden, Compact, Peek, Expanded }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;
    private const int WmNcHitTest = 0x0084;
    private const int WmHotkey = 0x0312;
    private const int HtTransparent = -1;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkE = 0x45;
    private const int HotkeyExpand = 4;
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private const int WmQuit = 0x0012;

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _syncTimer;

    private TranslateTransform _slide = new(0, -100);
    private TranslateTransform _seekHead = new();
    private TranslateTransform _expandedTitleSlide = new();
    private ScaleTransform _coverAScale = new(1, 1);
    private ScaleTransform _coverBScale = new(0.9, 0.9);
    private IslandState _state = IslandState.Hidden;
    private bool _hovering;
    private bool _seeking;
    private bool _fullscreen;
    private bool _showCoverB;
    private int _lastTrackRevision = -1;
    private bool _lastPlaying;
    private bool _clickThrough;
    private bool _altHeld;
    private bool _stickyExpanded;
    private double _targetOpacity = 1;
    private double _displayProgress;
    private bool _volumeDragging;
    private Grid? _volumeTrack;
    private Border? _volumeFill;
    private bool _dragging;
    private WinPoint _dragOrigin;
    private int _dragWindowX;
    private int _dragWindowY;
    private bool _hasPinnedPosition;
    private WinPoint _pinnedPosition;
    private Rect _pillRect;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    private static MainWindow? _hookOwner;
    private static LowLevelMouseProc? _mouseProc;
    private static IntPtr _mouseHook;
    private static int _hookThreadId;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => { _vm.Tick(); UpdateSeekUI(); };

        // Hover is polled from GetCursorPos, not from WPF mouse events: a click-through
        // window receives none. 16 ms also gives the opacity easing a smooth 60 fps cadence.
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTimer.Tick += (_, _) =>
        {
            PollCursor();
            if (Math.Abs(Shell.Opacity - _targetOpacity) > 0.01)
                Shell.Opacity += (_targetOpacity - Shell.Opacity) * 0.22;
            else
                Shell.Opacity = _targetOpacity;
        };

        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (!_seeking && !_hovering) Leave(); };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreen();

        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _volumeTimer.Tick += (_, _) =>
        {
            if (!_volumeDragging) _vm.RefreshVolume();
            UpdateVolumeUI();
        };

        // Media events can be sparse; re-read the timeline four times a second.
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncTimer.Tick += (_, _) => _vm.SyncTimeline();

        Pill.SizeChanged += (_, _) => UpdateShape();
        GlowPath.HorizontalAlignment = ShadowPath.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        GlowPath.VerticalAlignment = ShadowPath.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        GlowPath.Margin = ShadowPath.Margin = new Thickness(0, 9, 0, 0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        // AllowsTransparency already sets WS_EX_LAYERED; declare it explicitly for the fade.
        _ = SetWindowLongPtr(hwnd, GwlExStyle,
            new IntPtr(style | WsExToolWindow | WsExNoActivate | WsExLayered));
        HwndSource.FromHwnd(hwnd)?.AddHook(HwndHook);
        RegisterHotKey(hwnd, 1, ModControl | ModAlt, VkSpace);
        RegisterHotKey(hwnd, 2, ModControl | ModAlt, VkRight);
        RegisterHotKey(hwnd, 3, ModControl | ModAlt, VkLeft);
        RegisterHotKey(hwnd, HotkeyExpand, ModControl | ModAlt, VkE);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            if (MainViewModel.HotkeysEnabled)
            {
                switch (wParam.ToInt32())
                {
                    case 1: _vm.PlayPauseCommand.Execute(null); break;
                    case 2: _vm.NextCommand.Execute(null); break;
                    case 3: _vm.PreviousCommand.Execute(null); break;
                    case HotkeyExpand: ToggleStickyExpanded(); break;
                }
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmNcHitTest) return IntPtr.Zero;

        // Outside the pill the window must never swallow clicks meant for the app behind it.
        if (_state == IslandState.Hidden)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        int raw = lParam.ToInt32();
        var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
        if (!GetPillLayoutRect().Contains(screen))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }
        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        UnfreezeAnimatedTransforms();
        UpdateShape();
        PositionAtTopCenter();
        SetCover(CoverAArt, _vm.AlbumArt);
        UpdateAccent(_vm.AccentColor);

        _renderTimer.Start();
        _cursorTimer.Start();
        _fullscreenTimer.Start();
        _volumeTimer.Start();
        _syncTimer.Start();
        StartMouseHook();
        UpdateVolumeUI();

        if (!MainViewModel.AutoHide) Show(IslandState.Compact, false);

        _ = _vm.InitializeAsync();
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _renderTimer.Stop();
        _cursorTimer.Stop();
        _leaveTimer.Stop();
        _fullscreenTimer.Stop();
        _volumeTimer.Stop();
        _syncTimer.Stop();
        _ = _vm.DisposeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            for (int id = 1; id <= 3; id++) UnregisterHotKey(hwnd, id);
            UnregisterHotKey(hwnd, HotkeyExpand);
        }
        _hookOwner = null;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        base.OnClosed(e);
    }

    // The island never activates, so Windows sends wheel messages to the focused app.
    // A low-level hook is the only way to catch scrolling while it sits over the pill.
    private void StartMouseHook()
    {
        _hookOwner = this;
        _mouseProc = MouseHookCallback;
        var thread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            try
            {
                _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
                var msg = new NativeMessage();
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
                _hookThreadId = 0;
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private static IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        var owner = _hookOwner;
        if (code >= 0 && wParam == (IntPtr)WmMouseWheel && owner is not null)
        {
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if (owner._pillRect.Contains(new Point(data.Point.X, data.Point.Y)))
            {
                int delta = (short)((data.MouseData >> 16) & 0xFFFF);
                owner.Dispatcher.BeginInvoke(new Action(() => owner._vm.ChangeVolume(Math.Sign(delta) * 5)));
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsPlaying):
                _lastPlaying = _vm.IsPlaying;
                break;
            case nameof(MainViewModel.AccentColor):
                UpdateAccent(_vm.AccentColor);
                break;
            // A browser often publishes the thumbnail after the title. The expanded
            // covers are painted in code, so they need this explicit refresh.
            case nameof(MainViewModel.AlbumArt):
                CrossfadeCovers(_vm.AlbumArt);
                break;
            case nameof(MainViewModel.TrackRevision):
                OnTrackChanged();
                break;
            case nameof(MainViewModel.IsActive):
                if (!_vm.IsActive) Hide();
                else if (!_altHeld && !_stickyExpanded) Show(IslandState.Compact);
                break;
            case nameof(MainViewModel.Volume):
            case nameof(MainViewModel.IsMuted):
            case nameof(MainViewModel.VolumeGlyph):
                UpdateVolumeUI();
                break;
        }
    }

    private void UpdateShape()
    {
        double w = Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.Width;
        double h = Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.Height;
        if (w <= 0 || h <= 0) return;

        double r = h / 2;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(r, 0), true, true);
            ctx.LineTo(new Point(w - r, 0), true, true);
            ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(w, h - r), true, true);
            ctx.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(r, h), true, true);
            ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(0, r), true, true);
            ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            ctx.Close();
        }
        geo.Freeze();
        foreach (var path in new[] { GlowPath, ShadowPath, BodyPath, EdgePath })
        {
            path.Data = geo;
            path.Width = w;
            path.Height = h;
        }
        PillClip.Rect = new Rect(0, 0, w, h);
        PillClip.RadiusX = PillClip.RadiusY = r;
    }

    // XAML style values are frozen; animations need live Freezables.
    private void UnfreezeAnimatedTransforms()
    {
        SlideHost.RenderTransform = _slide;
        SeekHeadHost.RenderTransform = _seekHead;
        TitleTranslate.RenderTransform = _expandedTitleSlide;
        CoverA.RenderTransform = _coverAScale;
        CoverB.RenderTransform = _coverBScale;

        CompactTitle.RenderTransform = new TranslateTransform();
        ExpandedTitle.RenderTransform = new TranslateTransform();

        foreach (var glyph in new[] { PeekPlayGlyph, PeekPauseGlyph, ExpandedPlayGlyph, ExpandedPauseGlyph })
            glyph.RenderTransform = new ScaleTransform(1, glyph == PeekPauseGlyph || glyph == ExpandedPauseGlyph ? 0.6 : 1);
    }

    private void UpdateAccent(Color color)
        => _vm.AccentBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(350))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    private void OnTrackChanged()
    {
        if (_lastTrackRevision == _vm.TrackRevision) return;
        _lastTrackRevision = _vm.TrackRevision;

        Animate(_expandedTitleSlide, TranslateTransform.YProperty, 10, 0, 240);

        Dispatcher.BeginInvoke(new Action(() => { StartMarquee(CompactTitle); StartMarquee(ExpandedTitle); }));
    }

    private void CrossfadeCovers(ImageSource? art)
    {
        var incoming = _showCoverB ? CoverA : CoverB;
        var outgoing = _showCoverB ? CoverB : CoverA;
        var incomingScale = _showCoverB ? _coverAScale : _coverBScale;
        var outgoingScale = _showCoverB ? _coverBScale : _coverAScale;
        SetCover(_showCoverB ? CoverAArt : CoverBArt, art);

        var sb = new Storyboard();
        AddDouble(sb, outgoing, "Opacity", 1, 0, 220);
        AddDouble(sb, incoming, "Opacity", 0, 1, 260, 30);
        sb.Begin(this, true);

        Animate(outgoingScale, ScaleTransform.ScaleXProperty, 1, 0.9, 220);
        Animate(outgoingScale, ScaleTransform.ScaleYProperty, 1, 0.9, 220);
        Animate(incomingScale, ScaleTransform.ScaleXProperty, 0.9, 1, 260, 30);
        Animate(incomingScale, ScaleTransform.ScaleYProperty, 0.9, 1, 260, 30);
        _showCoverB = !_showCoverB;
    }

    // Leave the art layer transparent when there is no thumbnail so the note shows through.
    private static void SetCover(Border cover, ImageSource? art)
        => cover.Background = art is null
            ? null
            : new ImageBrush(art) { Stretch = Stretch.UniformToFill };

    private static void AddDouble(Storyboard sb, DependencyObject target, string property,
        double from, double to, int ms, int delay = 0, IEasingFunction? easing = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        sb.Children.Add(anim);
    }

    private static void Animate(DependencyObject target, DependencyProperty property,
        double from, double to, int ms, int delay = 0, IEasingFunction? easing = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (target is Animatable freezable) freezable.BeginAnimation(property, anim);
        else ((UIElement)target).BeginAnimation(property, anim);
    }

    private static void StartMarquee(TextBlock text)
    {
        if (text.RenderTransform is not TranslateTransform transform) return;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        text.UpdateLayout();
        var viewport = text.Parent as FrameworkElement;
        double overflow = text.ActualWidth - (viewport?.ActualWidth ?? 0);
        if (overflow <= 1) return;

        var marquee = new DoubleAnimation(0, -overflow - 24, TimeSpan.FromSeconds((overflow + 24) / 28.0))
        {
            BeginTime = TimeSpan.FromSeconds(1.5),
            RepeatBehavior = RepeatBehavior.Forever
        };
        transform.BeginAnimation(TranslateTransform.XProperty, marquee);
    }

    private void Show(IslandState target, bool immediate = false)
    {
        if (_state == target) return;
        _state = target;

        if (target == IslandState.Hidden)
        {
            _targetOpacity = 1;
            Shell.Opacity = 1;
            SetClickThrough(false);
        }
        else if (target == IslandState.Expanded)
        {
            // The controller must receive clicks while it is open.
            _targetOpacity = 1;
            SetClickThrough(false);
        }

        double width = target switch
        {
            IslandState.Compact or IslandState.Hidden => 220,
            IslandState.Peek => 320,
            _ => 400
        };
        double height = target switch
        {
            IslandState.Compact or IslandState.Hidden => 36,
            IslandState.Peek => 56,
            _ => 150
        };

        int ms = immediate ? 0 : 420;
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };

        var sb = new Storyboard();
        AddDouble(sb, Pill, "Width", Pill.Width, width, ms, 0, spring);
        AddDouble(sb, Pill, "Height", Pill.Height, height, ms, 0, spring);
        foreach (var path in new[] { GlowPath, ShadowPath, BodyPath, EdgePath })
        {
            AddDouble(sb, path, "Width", path.Width, width, ms, 0, spring);
            AddDouble(sb, path, "Height", path.Height, height, ms, 0, spring);
        }
        PillClip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(new Rect(0, 0, width, height), TimeSpan.FromMilliseconds(ms))
            { EasingFunction = spring });
        Animate(PillClip, RectangleGeometry.RadiusXProperty, PillClip.RadiusX, height / 2, ms, 0, spring);
        Animate(PillClip, RectangleGeometry.RadiusYProperty, PillClip.RadiusY, height / 2, ms, 0, spring);
        Animate(_slide, TranslateTransform.YProperty, _slide.Y, target == IslandState.Hidden ? -100 : 0, ms, 0, spring);
        sb.Begin(this, true);

        SetLayer(CompactRoot, target == IslandState.Compact);
        SetLayer(PeekRoot, target == IslandState.Peek);
        SetLayer(ExpandedRoot, target == IslandState.Expanded);
        ExpandedRoot.IsHitTestVisible = target == IslandState.Expanded;
        PeekRoot.IsHitTestVisible = target == IslandState.Peek;

        if (target != IslandState.Expanded)
        {
            _seeking = false;
            SeekTip.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetLayer(UIElement layer, bool visible)
    {
        layer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(160)));
    }

    private new void Hide() => Show(IslandState.Hidden);

    private void Leave()
    {
        if (!MainViewModel.AutoHide || _state == IslandState.Hidden) return;
        if (_state == IslandState.Expanded) Show(IslandState.Compact);
        else Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public WinPoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public int Time;
        public WinPoint Point;
    }

    private void PollCursor()
    {
        if (!GetCursorPos(out WinPoint cursor)) return;

        // GetAsyncKeyState is process-independent; GetKeyState only sees this thread's queue.
        bool hold = (GetAsyncKeyState(VkMenu) & 0x8000) != 0
                    || (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        if (hold && !_altHeld)
        {
            _altHeld = true;
            if (_vm.IsActive) Show(IslandState.Expanded);
            _targetOpacity = 1;
            SetClickThrough(false);
        }
        else if (!hold && _altHeld)
        {
            _altHeld = false;
            if (!_stickyExpanded) Show(IslandState.Compact);
        }

        var area = GetTargetScreen()?.Bounds ?? default;
        bool inZone = area.Width > 0
                      && cursor.X >= area.Left + area.Width / 2 - 100
                      && cursor.X <= area.Left + area.Width / 2 + 100
                      && cursor.Y >= area.Top && cursor.Y <= area.Top + 10;

        _pillRect = GetPillLayoutRect();
        bool inPill = _pillRect.Width > 0
                      && cursor.X >= _pillRect.Left && cursor.X <= _pillRect.Right
                      && cursor.Y >= _pillRect.Top && cursor.Y <= _pillRect.Bottom;

        if (_state == IslandState.Expanded || _stickyExpanded)
        {
            // Buttons must stay clickable while the controller is open.
            _hovering = inPill;
            _leaveTimer.Stop();
            _targetOpacity = 1;
            SetClickThrough(false);
            return;
        }

        if (_fullscreen && !inZone && !inPill)
        {
            _hovering = false;
            Hide();
            return;
        }

        if (inPill)
        {
            _hovering = true;
            _leaveTimer.Stop();
            if (MainViewModel.DodgeOnHover)
            {
                // Once WS_EX_TRANSPARENT is set the pill gets no mouse events at all,
                // so leaving is detected by this same poll against GetWindowRect.
                _targetOpacity = 0;
                SetClickThrough(true);
            }
            else
            {
                _targetOpacity = 1;
                SetClickThrough(false);
            }
            return;
        }

        if (inZone)
        {
            // Top-center zone is the deliberate escape hatch from fullscreen suppression.
            _hovering = true;
            _leaveTimer.Stop();
            _targetOpacity = 1;
            SetClickThrough(false);
            if (_state == IslandState.Hidden) Show(IslandState.Compact);
            return;
        }

        if (_seeking) FinishSeek();
        _targetOpacity = 1;
        SetClickThrough(false);

        if (_hovering)
        {
            _hovering = false;
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private void ToggleStickyExpanded()
    {
        _stickyExpanded = !_stickyExpanded;
        if (_stickyExpanded)
        {
            if (_vm.IsActive) Show(IslandState.Expanded);
            _targetOpacity = 1;
            SetClickThrough(false);
        }
        else if (!_altHeld)
        {
            Show(IslandState.Compact);
        }
    }

    // Layout position, not the animated one, so a faded pill still owns its hover area.
    private Rect GetPillLayoutRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out WinRect win)) return default;

        double scale = VisualTreeHelper.GetDpi(this).PixelsPerInchX / 96.0;
        double width = (Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.Width) * scale;
        double height = (Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.Height) * scale;
        double windowWidth = (ActualWidth > 0 ? ActualWidth : Width) * scale;
        return new Rect(win.Left + (windowWidth - width) / 2, win.Top + 9 * scale, width, height);
    }

    private void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _clickThrough == enabled) return;

        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        long next = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        if (next != style)
        {
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next));
            // WS_EX_TRANSPARENT is cached by the window manager until the frame changes.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNosize | SwpNomove | SwpNozorder | SwpNoactivate | SwpFramechanged);
        }
        _clickThrough = enabled;
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;
        _leaveTimer.Stop();
        Show(_state == IslandState.Expanded ? IslandState.Peek : IslandState.Expanded);
    }

    private static bool HasButtonAncestor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.ChangeVolume(Math.Sign(e.Delta) * 5);
        e.Handled = true;
    }

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleMute();
        e.Handled = true;
    }

    private void VolumeHost_MouseEnter(object sender, MouseEventArgs e)
    {
        Animate(PeekVolumeHost, FrameworkElement.WidthProperty, PeekVolumeHost.ActualWidth, 116, 200);
        Animate(PeekVolumePanel, FrameworkElement.WidthProperty, PeekVolumePanel.ActualWidth, 86, 200);
    }

    private void VolumeHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_volumeDragging) return;
        Animate(PeekVolumeHost, FrameworkElement.WidthProperty, PeekVolumeHost.ActualWidth, 30, 180);
        Animate(PeekVolumePanel, FrameworkElement.WidthProperty, PeekVolumePanel.ActualWidth, 0, 180);
    }

    private void UpdateVolumeUI()
    {
        PeekVolumeGlyph.Text = _vm.VolumeGlyph;
        ExpandedVolumeGlyph.Text = _vm.VolumeGlyph;

        double width = PeekVolumeTrack.ActualWidth;
        if (width > 0 && (!_volumeDragging || _volumeTrack == PeekVolumeTrack))
            PeekVolumeFill.Width = _vm.Volume / 100 * width;

        width = ExpandedVolumeTrack.ActualWidth;
        if (width > 0 && (!_volumeDragging || _volumeTrack == ExpandedVolumeTrack))
            ExpandedVolumeFill.Width = _vm.Volume / 100 * width;
    }

    private void PeekVolume_Down(object sender, MouseButtonEventArgs e) => BeginVolume(PeekVolumeTrack, PeekVolumeFill, e);
    private void PeekVolume_Move(object sender, MouseEventArgs e) => MoveVolume(PeekVolumeTrack, e);
    private void PeekVolume_Up(object sender, MouseButtonEventArgs e) => EndVolume(e);
    private void ExpandedVolume_Down(object sender, MouseButtonEventArgs e) => BeginVolume(ExpandedVolumeTrack, ExpandedVolumeFill, e);
    private void ExpandedVolume_Move(object sender, MouseEventArgs e) => MoveVolume(ExpandedVolumeTrack, e);
    private void ExpandedVolume_Up(object sender, MouseButtonEventArgs e) => EndVolume(e);

    private void BeginVolume(Grid track, Border fill, MouseButtonEventArgs e)
    {
        _volumeDragging = true;
        _volumeTrack = track;
        _volumeFill = fill;
        _leaveTimer.Stop();
        track.CaptureMouse();
        MoveVolume(track, e);
        e.Handled = true;
    }

    private void MoveVolume(Grid track, MouseEventArgs e)
    {
        if (!_volumeDragging || _volumeTrack != track) return;
        double width = track.ActualWidth;
        if (width <= 0) return;
        double x = Math.Clamp(e.GetPosition(track).X, 0, width);
        _vm.SetVolume(x / width * 100);
        _volumeFill!.Width = x;
        e.Handled = true;
    }

    private void EndVolume(MouseButtonEventArgs e)
    {
        if (!_volumeDragging) return;
        _volumeTrack?.ReleaseMouseCapture();
        _volumeDragging = false;
        _volumeTrack = null;
        _volumeFill = null;
        _leaveTimer.Stop();
        _leaveTimer.Start();
        e.Handled = true;
    }

    private void Pill_MouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out WinPoint cursor)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out WinRect win)) return;
        _dragging = true;
        _dragOrigin = cursor;
        _dragWindowX = win.Left;
        _dragWindowY = win.Top;
        Pill.CaptureMouse();
        e.Handled = true;
    }

    private void Pill_MouseRightMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.RightButton != MouseButtonState.Pressed) return;
        if (!GetCursorPos(out WinPoint cursor)) return;
        int dx = cursor.X - _dragOrigin.X;
        int dy = cursor.Y - _dragOrigin.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _pinnedPosition = new WinPoint { X = _dragWindowX + dx, Y = _dragWindowY + dy };
        _hasPinnedPosition = true;
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            _pinnedPosition.X, _pinnedPosition.Y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }

    private void Pill_MouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOptions();
        e.Handled = true;
    }

    public void OpenOptions() => new OptionsWindow(_vm, this).Show();

    public void ApplyOptions()
    {
        _hasPinnedPosition = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PositionAtTopCenter();
            Dispatcher.BeginInvoke(new Action(UpdateShape));
        }));
        if (!MainViewModel.AutoHide) Show(IslandState.Compact, false);
        else if (_state == IslandState.Compact) Hide();
        if (MainViewModel.DodgeOnHover && _hovering)
        {
            _targetOpacity = 0;
            SetClickThrough(true);
        }
    }

    private void UpdateSeekUI()
    {
        double target = Math.Clamp(_vm.Progress, 0, 1);
        _displayProgress += (target - _displayProgress) * 0.18;

        double compactWidth = CompactProgressTrack.ActualWidth;
        if (compactWidth > 0)
            CompactProgressFill.Width = _displayProgress * compactWidth;

        if (_seeking) return;
        double width = SeekBar.ActualWidth;
        if (width <= 0) return;
        double x = _displayProgress * width;
        SeekFill.Width = x;
        _seekHead.X = x - 5;
    }

    private void SeekBar_Down(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsLive || !_vm.CanSeek) return;
        _seeking = true;
        _leaveTimer.Stop();
        SeekBar.CaptureMouse();
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }

    private void SeekBar_Move(object sender, MouseEventArgs e)
    {
        if (_seeking) UpdateSeekFromPointer(e);
    }

    private void SeekBar_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        UpdateSeekFromPointer(e);
        FinishSeek();
        e.Handled = true;
    }

    private void FinishSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        if (SeekBar.IsMouseCaptured) SeekBar.ReleaseMouseCapture();
        SeekTip.Visibility = Visibility.Collapsed;
    }

    private void UpdateSeekFromPointer(MouseEventArgs e)
    {
        double width = SeekBar.ActualWidth;
        if (width <= 0) return;

        double x = Math.Clamp(e.GetPosition(SeekBar).X, 0, width);
        var target = TimeSpan.FromSeconds(x / width * _vm.Duration.TotalSeconds);
        _ = _vm.SeekAsync(target);

        SeekFill.Width = x;
        _seekHead.X = x - 5;
        SeekTipText.Text = FormatTime(target);
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.Margin = new Thickness(Math.Clamp(x - 20, 0, Math.Max(0, width - 56)), -18, 0, 0);
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => { PositionAtTopCenter(); Dispatcher.BeginInvoke(new Action(UpdateShape)); }));

    private static Screen? GetTargetScreen()
    {
        var screens = Screen.AllScreens;
        int index = MainViewModel.MonitorIndex;
        if (index >= 0 && index < screens.Length) return screens[index];
        return Screen.PrimaryScreen ?? screens.FirstOrDefault();
    }

    private void PositionAtTopCenter()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (_hasPinnedPosition)
        {
            SetWindowPos(hwnd, IntPtr.Zero, _pinnedPosition.X, _pinnedPosition.Y, 0, 0,
                SwpNosize | SwpNozorder | SwpNoactivate);
            return;
        }

        var area = GetTargetScreen()?.Bounds ?? default;
        if (area.Width <= 0) return;
        double widthDip = ActualWidth > 0 ? ActualWidth : Width;
        int widthPx = (int)Math.Round(widthDip * VisualTreeHelper.GetDpi(this).PixelsPerInchX / 96.0);
        SetWindowPos(hwnd, IntPtr.Zero, area.Left + (area.Width - widthPx) / 2, area.Top,
            0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }

    private void UpdateFullscreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == hwnd || IsZoomed(foreground)
            || !GetWindowRect(foreground, out WinRect rect))
        {
            _fullscreen = false;
            return;
        }

        var bounds = Screen.FromHandle(foreground).Bounds;
        _fullscreen = rect.Left <= bounds.Left && rect.Top <= bounds.Top
                      && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(int threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
