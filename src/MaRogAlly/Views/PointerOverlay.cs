using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MaRogAlly.UI;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;

namespace MaRogAlly.Views;

/// <summary>Formes du curseur, reprises de celles de Windows.</summary>
public enum PointerShape { Arrow, Text, Hand, SizeWE, SizeNS, SizeNWSE, SizeNESW, SizeAll, Wait, Busy, No, Cross }

/// <summary>
/// Curseur de la souris à la manette : une forme aux coins arrondis (contour fin, intérieur blanc) dans une toute petite
/// fenêtre, toujours devant, que les clics traversent et qui ne prend jamais le premier plan. Comme celui de Windows, il
/// change de forme selon ce qu'il survole : main sur un lien, barre de texte, flèches de redimensionnement…
/// </summary>
public sealed class PointerOverlay : Window
{
    /// <summary>Marge autour du dessin (place de son ombre et de l'onde du clic).</summary>
    private const double Gap = 16;
    /// <summary>Épaisseur du contour : juste assez pour détacher le blanc d'un fond clair.</summary>
    internal const double Stroke = 1.15;
    /// <summary>Contour gris anthracite, opaque (un contour transparent adoucit les bords et donne une impression de flou).</summary>
    internal static readonly Color OutlineColor = Color.FromRgb(0x3A, 0x3D, 0x45);
    /// <summary>Point visé dans la fenêtre (unités du dessin) : les formes centrées débordent à gauche et en haut.</summary>
    private const double Anchor = Gap + 14;
    /// <summary>Diamètre de l'onde qui part du point visé au clic.</summary>
    private const double RippleSize = 30;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// Flèche inclinée vers le haut à gauche, pointe en (0, 0), aux coins bien arrondis (la pointe un peu moins, pour viser juste).
    /// Calculée à partir des sommets (0,0), (22,12.5), (11.5,16) et (4,26).
    /// </summary>
    private static readonly Geometry Arrow = Freeze(Geometry.Parse(
        "M 3.83,2.17 L 17.31,9.83 Q 22,12.5 16.88,14.21 L 16.62,14.29 Q 11.5,16 8.26,20.32 L 7.66,21.12 Q 4,26 3.07,19.97 L 0.67,4.35 Q 0,0 3.83,2.17 Z"));

    /// <summary>Barre de saisie de texte : une simple barre aux bouts arrondis, sans empattements, centrée sur le point visé.</summary>
    private static readonly Geometry TextBeam = Freeze(new RectangleGeometry(new Rect(-1.6, -11, 3.2, 22), 1.6, 1.6));

    /// <summary>
    /// Main qui pointe, légèrement inclinée comme la flèche : le bout de l'index est le point visé, les trois autres doigts
    /// repliés (deux petits plis les séparent), le pouce arrondi sur le côté, la paume aux formes douces.
    /// </summary>
    private static readonly Geometry Hand = Turned(Geometry.Parse(
        "M -2,2 A 2,2 0 0 1 2,2 L 2,6.8 " +
        "A 2.1,2.1 0 0 1 5.8,7.4 A 2,2 0 0 1 9.4,8.6 A 1.9,1.9 0 0 1 12.6,10.4 " +
        "L 12.6,15.4 C 12.6,20.2 10.2,23.4 6.8,23.4 L 3.6,23.4 C 1.4,23.4 -0.2,22.4 -1.4,20.8 " +
        "L -5.4,15.6 C -6.4,14.2 -5.2,12 -3.6,12.8 L -2,14 Z " +
        "M 5.8,8.2 L 5.8,10.2 M 9.4,9.4 L 9.4,11.2"), -8);

    /// <summary>Double flèche horizontale, centrée, toute en arrondis ; tournée pour les autres redimensionnements.</summary>
    private static readonly Geometry DoubleArrow = RoundedPolygon(
        (-12, 0, 2), (-6.2, -5.6, 1.6), (-6.2, -1.7, 0.8), (6.2, -1.7, 0.8), (6.2, -5.6, 1.6),
        (12, 0, 2), (6.2, 5.6, 1.6), (6.2, 1.7, 0.8), (-6.2, 1.7, 0.8), (-6.2, 5.6, 1.6));

    /// <summary>Croix à quatre flèches (déplacer), dans le même style.</summary>
    private static readonly Geometry FourArrows = RoundedPolygon(
        (0, -12.5, 1.8), (5, -7, 1.4), (1.7, -7, 0.7), (1.7, -1.7, 0.7), (7, -1.7, 0.7), (7, -5, 1.4),
        (12.5, 0, 1.8), (7, 5, 1.4), (7, 1.7, 0.7), (1.7, 1.7, 0.7), (1.7, 7, 0.7), (5, 7, 1.4),
        (0, 12.5, 1.8), (-5, 7, 1.4), (-1.7, 7, 0.7), (-1.7, 1.7, 0.7), (-7, 1.7, 0.7), (-7, 5, 1.4),
        (-12.5, 0, 1.8), (-7, -5, 1.4), (-7, -1.7, 0.7), (-1.7, -1.7, 0.7), (-1.7, -7, 0.7), (-5, -7, 1.4));

    /// <summary>Viseur fin, aux bouts ronds.</summary>
    private static readonly Geometry CrossHair = RoundedPolygon(
        (-1.5, -11, 1.5), (1.5, -11, 1.5), (1.5, -1.5, 0.6), (11, -1.5, 1.5), (11, 1.5, 1.5), (1.5, 1.5, 0.6),
        (1.5, 11, 1.5), (-1.5, 11, 1.5), (-1.5, 1.5, 0.6), (-11, 1.5, 1.5), (-11, -1.5, 1.5), (-1.5, -1.5, 0.6));

    /// <summary>
    /// Polygone aux coins arrondis : chaque sommet est remplacé par une courbe, de rayon propre à ce sommet
    /// (une pointe peut rester plus vive qu'un coin de base).
    /// </summary>
    private static Geometry RoundedPolygon(params (double X, double Y, double R)[] corners)
    {
        var n = corners.Length;
        Point At(int i) => new(corners[(i + n) % n].X, corners[(i + n) % n].Y);
        // Point sur le côté, à une distance r du sommet (au plus la moitié du côté, pour ne pas mordre sur le coin voisin).
        static Point Toward(Point from, Point to, double r)
        {
            var v = to - from;
            return from + v * (Math.Min(r, v.Length / 2) / v.Length);
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < n; i++)
            {
                var corner = At(i);
                var start = Toward(corner, At(i - 1), corners[i].R);
                if (i == 0) g.BeginFigure(start, true, true);
                else g.LineTo(start, true, true);
                g.QuadraticBezierTo(corner, Toward(corner, At(i + 1), corners[i].R), true, true);
            }
        }
        return Freeze(geometry);
    }

    /// <summary>Interdit : un cercle barré.</summary>
    private static readonly Geometry Forbidden = Freeze(new CombinedGeometry(GeometryCombineMode.Union,
        new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(), 10.5, 10.5), new EllipseGeometry(new Point(), 7, 7)),
        new RectangleGeometry(new Rect(-8, -1.8, 16, 3.6), 0, 0, new RotateTransform(45))));

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Turned(Geometry geometry, double angle)
    {
        var turned = geometry.Clone();
        turned.Transform = new RotateTransform(angle);
        turned.Freeze();
        return turned;
    }

    private static readonly Dictionary<PointerShape, Geometry> Drawings = new()
    {
        [PointerShape.Arrow] = Arrow,
        [PointerShape.Busy] = Arrow,
        [PointerShape.Text] = TextBeam,
        [PointerShape.Hand] = Hand,
        [PointerShape.SizeWE] = DoubleArrow,
        [PointerShape.SizeNS] = Turned(DoubleArrow, 90),
        [PointerShape.SizeNWSE] = Turned(DoubleArrow, 45),
        [PointerShape.SizeNESW] = Turned(DoubleArrow, -45),
        [PointerShape.SizeAll] = FourArrows,
        [PointerShape.Cross] = CrossHair,
        [PointerShape.No] = Forbidden,
        [PointerShape.Wait] = Geometry.Empty,
    };

    /// <summary>Curseurs de Windows (identifiants IDC_*) et la forme qui leur correspond.</summary>
    private static readonly (int Id, PointerShape Shape)[] SystemCursors =
    [
        (32512, PointerShape.Arrow), (32513, PointerShape.Text), (32514, PointerShape.Wait), (32515, PointerShape.Cross),
        (32516, PointerShape.Arrow), (32642, PointerShape.SizeNWSE), (32643, PointerShape.SizeNESW), (32644, PointerShape.SizeWE),
        (32645, PointerShape.SizeNS), (32646, PointerShape.SizeAll), (32648, PointerShape.No), (32649, PointerShape.Hand),
        (32650, PointerShape.Busy), (32651, PointerShape.Arrow), (32671, PointerShape.Hand), (32672, PointerShape.Hand),
    ];

    private static Dictionary<IntPtr, PointerShape>? _byHandle;

    /// <summary>
    /// Forme du curseur que Windows a choisi (handle lu par GetCursorInfo) : les curseurs du système sont partagés par
    /// toutes les applications, un même handle veut dire une même forme. Un curseur propre à une application : la flèche.
    /// </summary>
    public static PointerShape ShapeOf(IntPtr cursor)
    {
        if (_byHandle is null)
        {
            var map = new Dictionary<IntPtr, PointerShape>();
            foreach (var (id, shape) in SystemCursors)
                if (LoadCursor(IntPtr.Zero, new IntPtr(id)) is var handle && handle != IntPtr.Zero) map.TryAdd(handle, shape);
            _byHandle = map;
        }
        return _byHandle.TryGetValue(cursor, out var found) ? found : PointerShape.Arrow;
    }

    /// <summary>État caché (petit et transparent) : la première apparition est animée comme les suivantes.</summary>
    private readonly ScaleTransform _scale = new(0.3, 0.3);
    /// <summary>Enfoncement au clic, vers le point visé : le curseur « touche » le bouton.</summary>
    private readonly ScaleTransform _press = new(1, 1);
    /// <summary>Changement de forme : la nouvelle jaillit du point visé.</summary>
    private readonly ScaleTransform _morph = new(1, 1);
    private readonly ScaleTransform _ghostScale = new(1, 1);
    private readonly ScaleTransform _rippleScale = new(0.2, 0.2);
    /// <summary>Apparition de la roue d'attente : elle grandit depuis son centre avec un léger rebond.</summary>
    private readonly ScaleTransform _spinnerPop = new(0.5, 0.5);
    private readonly Canvas _body;
    private readonly Path _shape, _ghost, _shadow;
    private readonly PointerSpinner _spinner;
    private readonly PointerBusyDots _dots;
    private readonly Ellipse _ripple;
    private readonly double _size;
    private static readonly IEasingFunction Away = new CubicEase { EasingMode = EasingMode.EaseIn };
    private IntPtr _hwnd;
    /// <summary>Décalage du point visé dans la fenêtre, en pixels réels (lu par le fil qui déplace le curseur).</summary>
    private volatile int _tipX, _tipY;
    /// <summary>Vrai entre l'apparition et le début de la disparition (le curseur doit être là).</summary>
    private bool _shown;
    private PointerShape _current = PointerShape.Arrow;

    /// <summary>Taille réelle du curseur « normal », par rapport au dessin (un peu plus discret que lui).</summary>
    private const double BaseScale = 0.82;

    /// <param name="size">Taille du curseur (1 : normale).</param>
    public PointerOverlay(double size)
    {
        size *= BaseScale;
        _size = size;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Assez haute pour les points d'attente sous la flèche.
        Width = (Anchor + 26 + Gap) * size;
        Height = (Anchor + PointerBusyDots.Top + 8 + Gap / 2) * size;
        Left = -1000;
        Top = -1000;

        var outline = new SolidColorBrush(OutlineColor);
        outline.Freeze();
        Path Outlined(Transform transform) => new()
        {
            Data = Arrow, Fill = Brushes.White, Stroke = outline, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = transform,
        };
        _ghost = Outlined(_ghostScale);
        _ghost.Opacity = 0;
        _shape = Outlined(_morph);
        // Ombre douce : une copie floutée et décalée, sous le dessin. Le flou ne touche qu'elle, le curseur reste net.
        _shadow = new Path
        {
            Data = Arrow, Fill = Brushes.Black, Stroke = Brushes.Black, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.35,
            Effect = new BlurEffect { Radius = 3 },
            RenderTransform = new TransformGroup { Children = { _morph, new TranslateTransform(0.5, 1.2) } },
        };

        // Roue d'attente (« occupé »), centrée sur le point visé.
        _spinner = new PointerSpinner { Opacity = 0, RenderTransform = _spinnerPop };
        // « Démarrage en cours » : trois points qui sautillent sous la flèche.
        _dots = new PointerBusyDots { Opacity = 0 };
        Canvas.SetLeft(_dots, PointerBusyDots.Left);
        Canvas.SetTop(_dots, PointerBusyDots.Top);

        _body = new Canvas
        {
            Opacity = 0,
            // Grandit, rétrécit et s'enfonce depuis le point visé : il ne bouge jamais.
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(size, size), _scale, _press } },
            Children = { _shadow, _ghost, _shape, _spinner, _dots },
        };
        var tip = Anchor * size;
        Canvas.SetLeft(_body, tip);
        Canvas.SetTop(_body, tip);
        // Onde du clic, centrée sur le point visé, sous le curseur.
        _ripple = new Ellipse
        {
            Width = RippleSize * size,
            Height = RippleSize * size,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.6 * size,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rippleScale,
        };
        Canvas.SetLeft(_ripple, tip - RippleSize * size / 2);
        Canvas.SetTop(_ripple, tip - RippleSize * size / 2);
        Content = new Canvas { Children = { _ripple, _body } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
    }

    /// <summary>Place le point visé sur ce point de l'écran (pixels réels) ; fait apparaître le curseur s'il n'y était pas.</summary>
    public void ShowAt(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        _tipX = (int)Math.Round(Anchor * _size * dpi.DpiScaleX);
        _tipY = (int)Math.Round(Anchor * _size * dpi.DpiScaleY);
        // Reposé tout en haut à chaque fois : une fenêtre ouverte entre-temps ne doit pas passer devant.
        SetWindowPos(_hwnd, HwndTopmost, x - _tipX, y - _tipY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (_shown) return;
        _shown = true;
        if (!IsVisible) Show();
        Appear();
        UpdateSpinner();
    }

    /// <summary>
    /// Déplacement à chaque image, depuis le fil qui déplace le curseur : sans attendre le fil de l'interface (qui peut
    /// être occupé), la fenêtre est déplacée par Windows dès que possible.
    /// </summary>
    public void MoveAsync(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, IntPtr.Zero, x - _tipX, y - _tipY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// Change de forme, comme Windows : l'ancienne s'efface en rétrécissant pendant que la nouvelle jaillit du point visé
    /// avec un petit rebond.
    /// </summary>
    public void SetShape(PointerShape shape)
    {
        if (shape == _current) return;
        var before = _current;
        _current = shape;
        var drawing = Drawings[shape];
        if (!ReferenceEquals(drawing, Drawings[before]))
        {
            _ghost.Data = _shape.Data;
            _ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Anim.Ms(130)) { EasingFunction = Anim.Out });
            var shrink = new DoubleAnimation(1, 0.6, Anim.Ms(130)) { EasingFunction = Anim.Out };
            _ghostScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _ghostScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            _shape.Data = drawing;
            _shadow.Data = drawing;
            _shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.35, Anim.Ms(140)) { EasingFunction = Anim.Out });
            var grow = new DoubleAnimation(0.55, 1, Anim.Ms(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            _morph.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _morph.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _shape.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(140)) { EasingFunction = Anim.Out });
        }
        UpdateSpinner();
    }

    /// <summary>Roue d'attente affichée (ou en train d'apparaître).</summary>
    private bool _spinnerOn;

    /// <summary>
    /// « Occupé » : la roue apparaît en grandissant avec un léger rebond et disparaît en se résorbant.
    /// « Démarrage en cours » : trois points sautillent sous la flèche.
    /// </summary>
    private void UpdateSpinner()
    {
        var waiting = _current == PointerShape.Wait && _shown;
        if (waiting != _spinnerOn)
        {
            _spinnerOn = waiting;
            if (waiting)
            {
                _spinner.Running = true;
                var pop = new DoubleAnimation(0.5, 1, Anim.Ms(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
                _spinnerPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                _spinnerPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                _spinner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim.Ms(160)) { EasingFunction = Anim.Out });
            }
            else
            {
                var shrink = new DoubleAnimation(0.5, Anim.Ms(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                _spinnerPop.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                _spinnerPop.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                var fade = new DoubleAnimation(0, Anim.Ms(160)) { EasingFunction = Anim.Out };
                fade.Completed += (_, _) => { if (!_spinnerOn) _spinner.Running = false; };
                _spinner.BeginAnimation(OpacityProperty, fade);
            }
        }
        var busy = _current == PointerShape.Busy && _shown;
        if (busy) _dots.Start();
        else _dots.Stop();
        _dots.BeginAnimation(OpacityProperty, new DoubleAnimation(busy ? 1 : 0, Anim.Ms(150)) { EasingFunction = Anim.Out });
    }

    /// <summary>
    /// Apparition bien visible : le curseur jaillit du point visé, tout petit, grossit nettement trop, puis se pose ;
    /// sans onde (elle est réservée au clic).
    /// </summary>
    private void Appear()
    {
        static KeyTime At(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(Math.Min(_scale.ScaleX, 0.1), At(0)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, At(230), new CubicEase { EasingMode = EasingMode.EaseOut }));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, At(480), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim.Ms(180)) { EasingFunction = Anim.Out });

    }

    /// <summary>Onde qui part du point visé et s'efface.</summary>
    private void Ripple(double reach, double ms)
    {
        var spread = new DoubleAnimation(0.2, reach, Anim.Ms(ms)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        _rippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
        _rippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
        _ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, Anim.Ms(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    /// <summary>
    /// Clic : le curseur s'enfonce vers le point visé comme s'il touchait le bouton, et une onde en part.
    /// Au relâchement, il se relève avec un petit rebond.
    /// </summary>
    public void Press(bool down)
    {
        // La barre de saisie reste immobile : on vise une position entre deux lettres (l'onde du clic reste).
        if (down && _current == PointerShape.Text)
        {
            Ripple(1, 440);
            return;
        }
        var to = down ? 0.82 : 1.0;
        var sink = new DoubleAnimation(to, Anim.Ms(down ? 90 : 240))
        {
            EasingFunction = down ? new CubicEase { EasingMode = EasingMode.EaseOut } : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, sink);
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, sink);
        if (down) Ripple(1, 440);
    }

    /// <summary>
    /// Repasse tout devant, sans attendre le fil de l'interface : un menu contextuel ou la barre des tâches, ouverts après
    /// lui, passeraient sinon par-dessus le curseur.
    /// </summary>
    public void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Disparition : le curseur rétrécit vers le point visé en s'effaçant, puis la fenêtre se cache.</summary>
    public void HidePointer()
    {
        if (!_shown) return;
        _shown = false;
        Animate(0.3, 200, Away, 0, 200, () =>
        {
            if (_shown) return;
            Hide();
            UpdateSpinner();
        });
    }

    private void Animate(double scale, double ms, IEasingFunction ease, double opacity, double fadeMs, Action? done)
    {
        // Repart de l'état du moment : une apparition interrompue par une disparition (ou l'inverse) ne saute pas.
        var grow = new DoubleAnimation(scale, Anim.Ms(ms)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimation(opacity, Anim.Ms(fadeMs)) { EasingFunction = Anim.Out };
        if (done is not null) fade.Completed += (_, _) => done();
        _body.BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
}
