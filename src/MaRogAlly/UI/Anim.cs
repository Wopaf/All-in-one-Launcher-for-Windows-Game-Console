using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaRogAlly.UI;

/// <summary>Petites animations réutilisables (toutes accélérées GPU : opacité et transformations).</summary>
public static class Anim
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
    public static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 };

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    public static void Fade(UIElement e, double to, double ms = 250, Action? done = null)
    {
        var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = Out };
        if (done is not null) a.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>Apparition : fondu + glissement depuis (dx, dy).</summary>
    public static void Enter(UIElement e, double dx = 0, double dy = 24, double ms = 380, double delay = 0)
    {
        var t = Translate(e);
        var begin = TimeSpan.FromMilliseconds(delay);
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(ms)) { EasingFunction = Out, BeginTime = begin });
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, Ms(ms)) { EasingFunction = Out, BeginTime = begin });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Ms(ms)) { EasingFunction = Out, BeginTime = begin });
    }

    /// <summary>Retour d'un élément en train de disparaître : il repart de là où il en est, sans saut.</summary>
    public static void Resume(UIElement e, double ms = 380)
    {
        var t = Translate(e);
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Ms(ms)) { EasingFunction = Out });
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Ms(ms)) { EasingFunction = Out });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(ms)) { EasingFunction = Out });
    }

    public static void Exit(UIElement e, double dx = 0, double dy = 0, double ms = 200, Action? done = null)
    {
        var t = Translate(e);
        var fade = new DoubleAnimation(0, Ms(ms)) { EasingFunction = InOut };
        if (done is not null) fade.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, fade);
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, Ms(ms)) { EasingFunction = InOut });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, Ms(ms)) { EasingFunction = InOut });
    }

    /// <summary>Animation d'une valeur quelconque, image par image (ex. défilement d'un ScrollViewer).</summary>
    public static void Tween(FrameworkElement owner, string slot, double from, double to, double ms, Action<double> apply)
    {
        Hosts.GetValue(owner, _ => new TweenHost()).Run(slot, from, to, ms, apply);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, TweenHost> Hosts = new();

    private static TranslateTransform Translate(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        if (e.RenderTransform is TransformGroup g && g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } inGroup) return inGroup;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }

    /// <summary>Anime une valeur arbitraire via une DependencyProperty factice.</summary>
    private sealed class TweenHost
    {
        private readonly Dictionary<string, TweenValue> _values = [];

        public void Run(string slot, double from, double to, double ms, Action<double> apply)
        {
            if (!_values.TryGetValue(slot, out var v)) _values[slot] = v = new TweenValue();
            v.Apply = apply;
            v.BeginAnimation(TweenValue.ValueProperty, new DoubleAnimation(from, to, Ms(ms)) { EasingFunction = Out });
        }
    }

    private sealed class TweenValue : Animatable
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(TweenValue),
            new PropertyMetadata(0.0, (d, e) => ((TweenValue)d).Apply?.Invoke((double)e.NewValue)));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public Action<double>? Apply { get; set; }
        protected override Freezable CreateInstanceCore() => new TweenValue();
    }

    /// <summary>Défilement vertical animé d'un ScrollViewer.</summary>
    public static void ScrollTo(ScrollViewer sv, double offset, double ms = 320)
    {
        offset = Math.Clamp(offset, 0, sv.ScrollableHeight);
        if (Math.Abs(offset - sv.VerticalOffset) < 0.5) return;
        Tween(sv, "v", sv.VerticalOffset, offset, ms, sv.ScrollToVerticalOffset);
    }

    /// <summary>Défilement horizontal animé d'un ScrollViewer.</summary>
    public static void ScrollToH(ScrollViewer sv, double offset, double ms = 320)
    {
        offset = Math.Clamp(offset, 0, sv.ScrollableWidth);
        if (Math.Abs(offset - sv.HorizontalOffset) < 0.5) return;
        Tween(sv, "h", sv.HorizontalOffset, offset, ms, sv.ScrollToHorizontalOffset);
    }
}
