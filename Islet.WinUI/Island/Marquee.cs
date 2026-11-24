using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Islet;

// Scrolls a title that does not fit: rest 1.5 s, scroll, repeat. Edges fade only while it scrolls.
// Everything moves on the composition thread; the UI thread does no per-frame work.
//
// Layout it expects: viewport > sourceClip > source (horizontal StackPanel) > text, plus fadeHost on top.
// While scrolling, sourceClip hides the plain text and fadeHost shows the same visuals through a
// gradient mask (a CompositionVisualSurface of source). Titles that fit are shown plain and crisp.
internal sealed class Marquee
{
    private const double Gap = 32;          // Blank space before the title comes round again, in DIPs
    private const double SpeedDipsPerSec = 30;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.5);

    private readonly TextBlock _text;
    private readonly FrameworkElement _viewport;
    private readonly FrameworkElement _sourceClip;
    private readonly FrameworkElement _source;
    private readonly FrameworkElement _fadeHost;
    private readonly Compositor _compositor;
    private Vector3KeyFrameAnimation? _scroll;
    private SpriteVisual? _faded;
    private CompositionVisualSurface? _surface;
    private bool _fading;

    public Marquee(TextBlock text, FrameworkElement viewport, FrameworkElement sourceClip, FrameworkElement source, FrameworkElement fadeHost)
    {
        _text = text;
        _viewport = viewport;
        _sourceClip = sourceClip;
        _source = source;
        _fadeHost = fadeHost;
        _compositor = ElementCompositionPreview.GetElementVisual(viewport).Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(text, true);
    }

    public bool IsScrolling => _scroll is not null;

    public void Update(bool paused)
    {
        Stop();

        double room = _viewport.ActualWidth;
        double height = _viewport.ActualHeight;
        if (room > 0) _viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, room, height) };

        bool still = AppSettings.Current.MotionReduced;
        _text.TextTrimming = still ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        _text.MaxWidth = still && room > 0 ? room : double.PositiveInfinity;

        _text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = _text.DesiredSize.Width - room;
        if (still || room <= 0 || overflow <= 1)
        {
            SetFading(false, room, height);
            return;
        }

        SetFading(true, room, height);
        if (paused) return;

        double distance = overflow + Gap;
        double speed = SpeedDipsPerSec * Math.Clamp(AppSettings.Current.AnimationSpeed, 0.5, 2);
        var total = Hold + TimeSpan.FromSeconds(distance / speed);
        var linear = _compositor.CreateLinearEasingFunction();

        // Every loop: rest at the start, then scroll left at a steady speed.
        var scroll = _compositor.CreateVector3KeyFrameAnimation();
        scroll.Target = "Translation";
        scroll.Duration = total;
        scroll.IterationBehavior = AnimationIterationBehavior.Forever;
        scroll.InsertKeyFrame(0f, Vector3.Zero, linear);
        scroll.InsertKeyFrame((float)(Hold / total), Vector3.Zero, linear);
        scroll.InsertKeyFrame(1f, new Vector3((float)-distance, 0, 0), linear);
        _text.StartAnimation(scroll);
        _scroll = scroll;
    }

    private void SetFading(bool on, double width, double height)
    {
        var clipVisual = ElementCompositionPreview.GetElementVisual(_sourceClip);
        if (!on)
        {
            if (_fading)
            {
                clipVisual.Clip = null;
                if (_faded is not null) _faded.IsVisible = false;
                _fading = false;
            }
            return;
        }

        if (_faded is null)
        {
            _surface = _compositor.CreateVisualSurface();
            _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_source);

            var mask = _compositor.CreateLinearGradientBrush();
            mask.StartPoint = new Vector2(0, 0);
            mask.EndPoint = new Vector2(1, 0);
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(0f, Color.FromArgb(0, 255, 255, 255)));
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(0.08f, Color.FromArgb(255, 255, 255, 255)));
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(0.92f, Color.FromArgb(255, 255, 255, 255)));
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));

            var text = _compositor.CreateSurfaceBrush(_surface);
            text.Stretch = CompositionStretch.None;
            text.HorizontalAlignmentRatio = 0;
            text.VerticalAlignmentRatio = 0;

            var masked = _compositor.CreateMaskBrush();
            masked.Source = text;
            masked.Mask = mask;

            _faded = _compositor.CreateSpriteVisual();
            _faded.Brush = masked;
            ElementCompositionPreview.SetElementChildVisual(_fadeHost, _faded);
        }

        var size = new Vector2((float)width, (float)height);
        _faded.Size = size;
        _surface!.SourceSize = size;
        _faded.IsVisible = true;
        // Hide the plain copy; the clip is on the parent, so the surface still sees the text.
        clipVisual.Clip = _compositor.CreateInsetClip(0, 0, (float)width + 1, 0);
        _fading = true;
    }

    public void Stop()
    {
        if (_scroll is not null)
        {
            _text.StopAnimation(_scroll);
            _scroll = null;
        }
        _text.Translation = Vector3.Zero;
    }
}
