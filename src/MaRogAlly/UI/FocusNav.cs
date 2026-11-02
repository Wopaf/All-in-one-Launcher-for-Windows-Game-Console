using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaRogAlly.Input;

namespace MaRogAlly.UI;

/// <summary>Navigation au focus WPF pour les menus et panneaux (réglages, tri, filtres, fiche de jeu).</summary>
public static class FocusNav
{
    /// <summary>Déplace le focus dans la direction demandée, sans sortir de <paramref name="scope"/>.</summary>
    public static bool Move(NavAction action, FrameworkElement scope)
    {
        var dir = action switch
        {
            NavAction.Up => FocusNavigationDirection.Up,
            NavAction.Down => FocusNavigationDirection.Down,
            NavAction.Left => FocusNavigationDirection.Left,
            NavAction.Right => FocusNavigationDirection.Right,
            _ => (FocusNavigationDirection?)null,
        };
        if (dir is null) return false;

        if (Keyboard.FocusedElement is not UIElement current || !IsInside(current, scope))
        {
            FocusFirst(scope);
            return true;
        }
        var target = current.PredictFocus(dir.Value) as UIElement;
        if (target is null || !IsInside(target, scope)) return false;
        target.Focus();
        BringIntoView(target);
        return true;
    }

    public static void FocusFirst(FrameworkElement scope)
    {
        scope.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        if (Keyboard.FocusedElement is FrameworkElement f && IsInside(f, scope)) BringIntoView(f);
    }

    public static void Focus(UIElement element)
    {
        element.Focus();
        if (element is FrameworkElement f) BringIntoView(f);
    }

    /// <summary>« Clique » l'élément qui a le focus (bouton A).</summary>
    public static bool Activate()
    {
        switch (Keyboard.FocusedElement)
        {
            case ToggleButton t:
                t.IsChecked = !(t.IsChecked ?? false);
                t.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, t));
                return true;
            case ButtonBase b:
                b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
                return true;
            default:
                return false;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject scope)
        => element == scope || (element is Visual v && scope is Visual s && v.IsDescendantOf(s));

    private static void BringIntoView(UIElement element)
    {
        if (element is FrameworkElement fe && FindScroll(fe) is { } sv)
        {
            var at = fe.TransformToAncestor(sv).Transform(new Point(0, 0));
            if (sv.ScrollableHeight > 0)
            {
                // Défilement doux pour garder l'élément au centre.
                Anim.ScrollTo(sv, at.Y + sv.VerticalOffset - (sv.ViewportHeight - fe.ActualHeight) / 2);
            }
            if (sv.ScrollableWidth > 0)
            {
                // Rangée horizontale : on ne défile que si l'élément sort de l'écran.
                var x = at.X + sv.HorizontalOffset;
                if (at.X < 64) Anim.ScrollToH(sv, x - 64);
                else if (at.X + fe.ActualWidth > sv.ViewportWidth - 64) Anim.ScrollToH(sv, x + fe.ActualWidth - sv.ViewportWidth + 64);
            }
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject e)
    {
        var p = System.Windows.Media.VisualTreeHelper.GetParent(e);
        while (p is not null and not ScrollViewer) p = System.Windows.Media.VisualTreeHelper.GetParent(p);
        return p as ScrollViewer;
    }
}
