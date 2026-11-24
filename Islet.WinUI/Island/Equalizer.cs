using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Shapes;

namespace Islet;

// Four bars that bounce while music plays and rest low when paused. Composition animations:
// they run off the UI thread and are stopped (not just hidden) whenever nobody can see them.
internal sealed class Equalizer
{
    private const float RestScale = 0.3f;
    private static readonly double[] Periods = { 0.42, 0.55, 0.36, 0.5 }; // Seconds; unequal so the bars never sync.

    private readonly Rectangle[] _bars;
    private readonly Vector3KeyFrameAnimation?[] _running;
    private readonly Compositor _compositor;
    private bool _animating;

    public Equalizer(Panel host)
    {
        _bars = host.Children.OfType<Rectangle>().ToArray();
        _running = new Vector3KeyFrameAnimation?[_bars.Length];
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        foreach (var bar in _bars)
        {
            bar.CenterPoint = new Vector3((float)bar.Width / 2, (float)bar.Height, 0); // Grow from the bottom.
            bar.Scale = new Vector3(1, RestScale, 1);
        }
    }

    public void SetFill(Microsoft.UI.Xaml.Media.Brush brush)
    {
        foreach (var bar in _bars) bar.Fill = brush;
    }

    // restart re-applies speed and reduce-motion after a settings change.
    public void Update(bool playing, bool paused, bool restart = false)
    {
        bool animate = playing && !paused && !AppSettings.Current.MotionReduced;
        if (animate && _animating && !restart) return;
        StopAll();

        if (!animate)
        {
            // Paused bars rest low; with reduce motion a playing icon shows a static, uneven shape.
            for (int i = 0; i < _bars.Length; i++)
                _bars[i].Scale = new Vector3(1, playing ? 0.45f + 0.15f * (i % 3) : RestScale, 1);
            return;
        }

        double speed = Math.Clamp(AppSettings.Current.AnimationSpeed, 0.5, 2);
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0), new Vector2(0.63f, 1)); // Sine in-out
        for (int i = 0; i < _bars.Length; i++)
        {
            var bounce = _compositor.CreateVector3KeyFrameAnimation();
            bounce.Target = "Scale";
            bounce.InsertKeyFrame(0f, new Vector3(1, RestScale, 1), ease);
            bounce.InsertKeyFrame(1f, Vector3.One, ease);
            bounce.Duration = TimeSpan.FromSeconds(Periods[i % Periods.Length] / speed);
            bounce.Direction = AnimationDirection.Alternate;
            bounce.IterationBehavior = AnimationIterationBehavior.Forever;
            _bars[i].StartAnimation(bounce);
            _running[i] = bounce;
        }
        _animating = true;
    }

    private void StopAll()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            if (_running[i] is { } animation) _bars[i].StopAnimation(animation);
            _running[i] = null;
        }
        _animating = false;
    }
}
