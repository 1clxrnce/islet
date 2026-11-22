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
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _fullscreenTimer;

    private Storyboard? _barsStoryboard;
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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => { _vm.Tick(); UpdateSeekUI(); };

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _cursorTimer.Tick += (_, _) => PollCursor();

        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (!_seeking && !_hovering) Leave(); };

        _peekTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); if (!_hovering) Leave(); };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreen();

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
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow | WsExNoActivate));
        HwndSource.FromHwnd(hwnd)?.AddHook(HwndHook);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;

        // Hidden is offscreen, and the hover zone is found by global cursor polling,
        // so the whole window can stay click-through until a pill state is shown.
        if (_state == IslandState.Hidden)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        int raw = lParam.ToInt32();
        var local = PointFromScreen(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
        var bounds = Pill.TransformToVisual(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        if (!bounds.Contains(local))
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
        BuildBars();
        UpdateShape();
        PositionAtTopCenter();
        SetCover(CoverA, _vm.AlbumArt);
        UpdateBars();
        UpdateAccent(_vm.AccentColor);

        _renderTimer.Start();
        _cursorTimer.Start();
        _fullscreenTimer.Start();

        if (!MainViewModel.AutoHide) Show(IslandState.Compact, false);

        try { _ = _vm.InitializeAsync(); }
        catch { /* media access can be denied; tray stays usable */ }
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _renderTimer.Stop();
        _cursorTimer.Stop();
        _leaveTimer.Stop();
        _peekTimer.Stop();
        _fullscreenTimer.Stop();
        _ = _vm.DisposeAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsPlaying):
                UpdateBars();
                if (_vm.IsPlaying && !_lastPlaying && _vm.IsActive) AutoPeek();
                _lastPlaying = _vm.IsPlaying;
                break;
            case nameof(MainViewModel.AccentColor):
                UpdateAccent(_vm.AccentColor);
                break;
            case nameof(MainViewModel.TrackRevision):
                OnTrackChanged();
                break;
            case nameof(MainViewModel.IsActive):
                if (!_vm.IsActive) Hide();
                else if (_vm.IsPlaying) AutoPeek();
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

        foreach (var bar in new[] { Bar1, Bar2, Bar3, Bar4 })
            bar.RenderTransform = new ScaleTransform(1, 0.18);

        foreach (var glyph in new[] { PeekPlayGlyph, PeekPauseGlyph, ExpandedPlayGlyph, ExpandedPauseGlyph })
            glyph.RenderTransform = new ScaleTransform(1, glyph == PeekPauseGlyph || glyph == ExpandedPauseGlyph ? 0.6 : 1);
    }

    private void BuildBars()
    {
        _barsStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        AddBarAnimation(Bar1, 310, 0);
        AddBarAnimation(Bar2, 470, 90);
        AddBarAnimation(Bar3, 390, 180);
        AddBarAnimation(Bar4, 530, 40);
    }

    private void AddBarAnimation(Rectangle bar, int durationMs, int delayMs)
    {
        var anim = new DoubleAnimation(0.18, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        _barsStoryboard!.Children.Add(anim);
    }

    private void UpdateBars()
    {
        if (_vm.IsPlaying)
        {
            StopBars();
            _barsStoryboard?.Begin(this, true);
        }
        else
        {
            StopBars();
            var stop = new Storyboard();
            foreach (var bar in new[] { Bar1, Bar2, Bar3, Bar4 })
            {
                var anim = new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, bar);
                Storyboard.SetTargetProperty(anim, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
                stop.Children.Add(anim);
            }
            stop.Begin(this, true);
        }
    }

    private void StopBars()
    {
        if (_barsStoryboard is null) return;
        _barsStoryboard.Stop(this);
        foreach (var bar in new[] { Bar1, Bar2, Bar3, Bar4 })
        {
            if (bar.RenderTransform is not ScaleTransform scale) continue;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = 0.18;
        }
    }

    private void UpdateAccent(Color color)
        => _vm.AccentBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(350))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    private void OnTrackChanged()
    {
        if (_lastTrackRevision == _vm.TrackRevision) return;
        _lastTrackRevision = _vm.TrackRevision;

        CrossfadeCovers(_vm.AlbumArt);
        Animate(_expandedTitleSlide, TranslateTransform.YProperty, 10, 0, 240);

        Dispatcher.BeginInvoke(new Action(() => { StartMarquee(CompactTitle); StartMarquee(ExpandedTitle); }));
        AutoPeek();
    }

    private void AutoPeek()
    {
        if (!MainViewModel.AutoHide || _fullscreen || _hovering || !_vm.IsActive) return;
        _peekTimer.Stop();
        Show(IslandState.Peek, false);
        _peekTimer.Start();
    }

    private void CrossfadeCovers(ImageSource? art)
    {
        var incoming = _showCoverB ? CoverA : CoverB;
        var outgoing = _showCoverB ? CoverB : CoverA;
        var incomingScale = _showCoverB ? _coverAScale : _coverBScale;
        var outgoingScale = _showCoverB ? _coverBScale : _coverAScale;
        SetCover(incoming, art);

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

    private static void SetCover(Border cover, ImageSource? art)
        => cover.Background = art is null
            ? new SolidColorBrush(Color.FromArgb(255, 32, 32, 36))
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

    private void PollCursor()
    {
        if (!GetCursorPos(out WinPoint cursor)) return;

        var area = Screen.PrimaryScreen?.Bounds ?? default;
        bool inZone = cursor.X >= area.Left + area.Width / 2 - 100
                      && cursor.X <= area.Left + area.Width / 2 + 100
                      && cursor.Y >= area.Top && cursor.Y <= area.Top + 10;

        var pillRect = GetPillScreenRect();
        bool inPill = cursor.X >= pillRect.Left && cursor.X <= pillRect.Right
                      && cursor.Y >= pillRect.Top && cursor.Y <= pillRect.Bottom;

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
            if (_state == IslandState.Compact) Show(IslandState.Peek);
            return;
        }

        if (inZone)
        {
            // The hover zone is the deliberate escape hatch from fullscreen suppression.
            _hovering = true;
            _leaveTimer.Stop();
            if (_state == IslandState.Hidden || _state == IslandState.Compact) Show(IslandState.Peek);
            return;
        }

        if (_seeking)
        {
            FinishSeek();
        }

        if (_hovering)
        {
            _hovering = false;
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private Rect GetPillScreenRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out WinRect win)) return default;

        double scale = VisualTreeHelper.GetDpi(this).PixelsPerInchX / 96.0;
        var local = Pill.TransformToVisual(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        return new Rect(
            win.Left + local.Left * scale,
            win.Top + local.Top * scale,
            local.Width * scale,
            local.Height * scale);
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _leaveTimer.Stop();
        if (_state == IslandState.Compact) Show(IslandState.Peek);
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        _leaveTimer.Stop();
        if (!_seeking) _leaveTimer.Start();
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

    private void UpdateSeekUI()
    {
        if (_seeking) return;
        double width = SeekBar.ActualWidth;
        if (width <= 0) return;
        double x = Math.Clamp(_vm.Progress, 0, 1) * width;
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

    private void PositionAtTopCenter()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var primary = Screen.PrimaryScreen;
        if (primary is null) return;

        var area = primary.Bounds;
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
}
