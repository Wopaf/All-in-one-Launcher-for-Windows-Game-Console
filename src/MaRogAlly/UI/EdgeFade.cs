using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaRogAlly.UI;

/// <summary>
/// Rangée horizontale : ses cartes s'estompent à gauche et à droite, de chaque côté seulement
/// quand une carte y est coupée par le bord. La marge du contenu (avant la première carte, après la dernière) n'en est pas une.
/// </summary>
public static class EdgeFade
{
    /// <summary>Largeur du dégradé. Égale à l'écart gardé autour de la sélection (voir FocusNav), elle n'estompe jamais la carte sélectionnée.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(EdgeFade), new PropertyMetadata(0.0, OnSizeChanged));

    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scroll && e.OldValue is 0.0) _ = new Mask(scroll);
    }

    private sealed class Mask
    {
        private const double Tolerance = 1;
        private static readonly Color Opaque = Colors.Black;
        private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

        private readonly ScrollViewer _scroll;
        private readonly GradientStop _leftEdge = new(Opaque, 0), _leftInner = new(Opaque, 0), _rightInner = new(Opaque, 1), _rightEdge = new(Opaque, 1);
        private bool _left, _right;

        public Mask(ScrollViewer scroll)
        {
            _scroll = scroll;
            scroll.OpacityMask = new LinearGradientBrush(new GradientStopCollection { _leftEdge, _leftInner, _rightInner, _rightEdge }, new Point(0, 0), new Point(1, 0));
            // Déclenché aussi quand la taille de la rangée ou son contenu changent.
            scroll.ScrollChanged += (_, _) => Update();
        }

        private void Update()
        {
            if (_scroll.ActualWidth <= 0) return;
            var share = Math.Min(GetSize(_scroll) / _scroll.ActualWidth, 0.5);
            _leftInner.Offset = share;
            _rightInner.Offset = 1 - share;

            var margin = (_scroll.Content as FrameworkElement)?.Margin ?? default;
            Show(_leftEdge, ref _left, _scroll.HorizontalOffset > margin.Left + Tolerance);
            Show(_rightEdge, ref _right, _scroll.ExtentWidth - margin.Right - _scroll.HorizontalOffset > _scroll.ViewportWidth + Tolerance);
        }

        private static void Show(GradientStop edge, ref bool shown, bool show)
        {
            if (shown == show) return;
            shown = show;
            edge.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(show ? Clear : Opaque, Anim.Ms(180)));
        }
    }
}
