using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MaRogAlly.Views;

/// <summary>
/// « Démarrage en cours » : trois points sous la flèche, qui sautillent chacun leur tour, comme quelqu'un qui tape un
/// message. L'origine est au centre du premier point.
/// </summary>
internal sealed class PointerBusyDots : Canvas
{
    /// <summary>Position des points sous la flèche (unités du dessin, la pointe de la flèche en (0, 0)).</summary>
    public const double Left = 5, Top = 31;
    private const double Radius = 2.1, Spacing = 5.4, Jump = 3.4;
    /// <summary>Durée d'un tour (les trois sauts puis une pause) et décalage entre deux points.</summary>
    private const double Cycle = 1.05, Stagger = 0.15;

    private readonly TranslateTransform[] _hops = new TranslateTransform[3];
    private bool _running;

    public PointerBusyDots()
    {
        var outline = new SolidColorBrush(PointerOverlay.OutlineColor);
        outline.Freeze();
        for (var i = 0; i < 3; i++)
        {
            _hops[i] = new TranslateTransform();
            var dot = new Ellipse
            {
                Width = 2 * Radius, Height = 2 * Radius,
                Fill = Brushes.White, Stroke = outline, StrokeThickness = PointerOverlay.Stroke,
                RenderTransform = _hops[i],
            };
            SetLeft(dot, i * Spacing - Radius);
            SetTop(dot, -Radius);
            Children.Add(dot);
        }
    }

    /// <summary>Encombrement des points, sauts compris, par rapport à l'origine.</summary>
    public static Rect Bounds => new(-Radius - 1, -Radius - Jump - 1, 2 * Spacing + 2 * Radius + 2, 2 * Radius + Jump + 2);

    public void Start()
    {
        if (_running) return;
        _running = true;
        for (var i = 0; i < 3; i++)
        {
            // Saut rapide vers le haut, retombée un peu plus douce, puis repos jusqu'au tour suivant.
            var hop = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(Cycle),
                BeginTime = TimeSpan.FromSeconds(i * Stagger),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            hop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-Jump, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.17)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.36)), new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 1, Bounciness = 4 }));
            hop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Cycle))));
            _hops[i].BeginAnimation(TranslateTransform.YProperty, hop);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        foreach (var hop in _hops) hop.BeginAnimation(TranslateTransform.YProperty, null);
    }
}
