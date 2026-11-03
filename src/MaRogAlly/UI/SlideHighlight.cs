using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaRogAlly.UI;

/// <summary>
/// Surlignage d'une liste qui glisse d'une ligne à l'autre (au lieu d'un fond qui saute).
/// Le cadre est posé dans la même grille que la liste, derrière elle ; les lignes portent
/// <see cref="RowOptions"/>.Floating pour ne plus dessiner leur propre fond de sélection.
/// </summary>
public sealed class SlideHighlight
{
    private readonly Border _box;
    private readonly FrameworkElement _host;
    private readonly TranslateTransform _move = new();
    private bool _shown;

    public SlideHighlight(Border box, FrameworkElement host)
    {
        _box = box;
        _host = host;
        box.RenderTransform = _move;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.VerticalAlignment = VerticalAlignment.Top;
        box.IsHitTestVisible = false;
        box.Opacity = 0;
    }

    /// <summary>Glisse jusqu'à la ligne (rapide et fluide), ou s'y place directement s'il était masqué.</summary>
    public void MoveTo(FrameworkElement row, double opacity = 1)
    {
        if (!row.IsVisible || row.ActualHeight <= 0)
        {
            // Ligne pas encore mesurée (liste reconstruite) : on réessaie une fois la mise en page faite.
            row.Dispatcher.BeginInvoke(() => { if (row.IsVisible && row.ActualHeight > 0) MoveTo(row, opacity); }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        var at = row.TranslatePoint(new Point(0, 0), _host);
        var y = at.Y;
        var width = row.ActualWidth;
        var height = row.ActualHeight;

        if (!_shown)
        {
            Stop();
            _move.X = at.X;
            _move.Y = y;
            _box.Width = width;
            _box.Height = height;
            _shown = true;
        }
        else
        {
            // Rapide, avec un léger rebond en arrivant sur la ligne… sauf si ce rebond faisait sortir
            // le cadre de la zone visible (il y serait coupé) : sur cet axe, arrivée sans dépassement.
            var bounds = VisibleBounds();
            var bounceX = Fits(_move.X, at.X, _box.ActualWidth, width, bounds.Left, bounds.Right);
            var bounceY = Fits(_move.Y, y, _box.ActualHeight, height, bounds.Top, bounds.Bottom);
            const double ms = 230;
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(at.X, Anim.Ms(ms)) { EasingFunction = bounceX ? Bounce : Smooth });
            _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Anim.Ms(ms)) { EasingFunction = bounceY ? Bounce : Smooth });
            _box.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(width, Anim.Ms(ms)) { EasingFunction = bounceX ? Bounce : Smooth });
            _box.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(height, Anim.Ms(ms)) { EasingFunction = bounceY ? Bounce : Smooth });
        }
        Anim.Fade(_box, opacity, 150);
    }

    private static readonly BackEase Bounce = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 };
    private static readonly CubicEase Smooth = new() { EasingMode = EasingMode.EaseOut };
    /// <summary>Dépassement maximal du rebond, en part du trajet.</summary>
    private static readonly double Overshoot = Enumerable.Range(0, 101).Max(i => Bounce.Ease(i / 100.0)) - 1;

    /// <summary>
    /// Vrai si, pendant le rebond, les deux bords du cadre (position et taille animées ensemble)
    /// restent dans [min, max].
    /// </summary>
    private static bool Fits(double fromPos, double toPos, double fromSize, double toSize, double min, double max)
    {
        var start = toPos + (toPos - fromPos) * Overshoot;
        var end = toPos + toSize + (toPos + toSize - fromPos - fromSize) * Overshoot;
        const double margin = 0.5;
        return start >= min - margin && end <= max + margin;
    }

    /// <summary>Zone où le cadre reste visible : la liste, réduite à la partie affichée si elle défile.</summary>
    private Rect VisibleBounds()
    {
        var rect = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
        for (var o = VisualTreeHelper.GetParent(_host); o is not null; o = VisualTreeHelper.GetParent(o))
        {
            if (o is not ScrollViewer sv) continue;
            var origin = sv.TranslatePoint(new Point(0, 0), _host);
            rect.Intersect(new Rect(origin, new Size(sv.ViewportWidth > 0 ? sv.ViewportWidth : sv.ActualWidth,
                sv.ViewportHeight > 0 ? sv.ViewportHeight : sv.ActualHeight)));
            break;
        }
        return rect.IsEmpty ? new Rect(0, 0, _host.ActualWidth, _host.ActualHeight) : rect;
    }

    /// <summary>Atténue le cadre sans le déplacer (ex. catégorie active quand on règle ses options).</summary>
    public void Dim(double opacity) { if (_shown) Anim.Fade(_box, opacity, 150); }

    public void Hide()
    {
        _shown = false;
        Anim.Fade(_box, 0, 120);
    }

    private void Stop()
    {
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        _move.BeginAnimation(TranslateTransform.YProperty, null);
        _box.BeginAnimation(FrameworkElement.WidthProperty, null);
        _box.BeginAnimation(FrameworkElement.HeightProperty, null);
    }
}
