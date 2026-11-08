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
    private const double Gap = 16, Stroke = 1.7;
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

    /// <summary>Barre de saisie de texte, centrée sur le point visé.</summary>
    private static readonly Geometry TextBeam = Freeze(Geometry.Parse(
        "M -5,-12 L 5,-12 L 5,-9 L 1.8,-9 L 1.8,9 L 5,9 L 5,12 L -5,12 L -5,9 L -1.8,9 L -1.8,-9 L -5,-9 Z"));

    /// <summary>Main qui pointe : le bout de l'index est le point visé, les autres doigts repliés, le pouce sur le côté.</summary>
    private static readonly Geometry Hand = Freeze(Geometry.Parse(
        "M -2.5,2.5 A 2.5,2.5 0 0 1 2.5,2.5 L 2.5,9.5 A 2.4,2.4 0 0 1 7.2,10.2 A 2.4,2.4 0 0 1 11.5,11.8 A 2,2 0 0 1 15,13.6 " +
        "L 15,18.5 Q 15,25.5 9,25.5 L 4,25.5 Q 0.5,25.5 -2,22.5 L -7.6,15.6 A 1.9,1.9 0 0 1 -4.8,13.2 L -2.5,15.5 Z"));

    /// <summary>Double flèche horizontale, centrée ; tournée pour les autres redimensionnements.</summary>
    private static readonly Geometry DoubleArrow = Freeze(Geometry.Parse(
        "M -12,0 L -6,-5.5 L -6,-2 L 6,-2 L 6,-5.5 L 12,0 L 6,5.5 L 6,2 L -6,2 L -6,5.5 Z"));

    /// <summary>Croix à quatre flèches (déplacer).</summary>
    private static readonly Geometry FourArrows = Freeze(Geometry.Parse(
        "M 0,-12 L 5,-6.5 L 2,-6.5 L 2,-2 L 6.5,-2 L 6.5,-5 L 12,0 L 6.5,5 L 6.5,2 L 2,2 L 2,6.5 L 5,6.5 L 0,12 L -5,6.5 L -2,6.5 " +
        "L -2,2 L -6.5,2 L -6.5,5 L -12,0 L -6.5,-5 L -6.5,-2 L -2,-2 L -2,-6.5 L -5,-6.5 Z"));

    /// <summary>Viseur fin.</summary>
    private static readonly Geometry CrossHair = Freeze(Geometry.Parse(
        "M -1.8,-11 L 1.8,-11 L 1.8,-1.8 L 11,-1.8 L 11,1.8 L 1.8,1.8 L 1.8,11 L -1.8,11 L -1.8,1.8 L -11,1.8 L -11,-1.8 L -1.8,-1.8 Z"));

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
    private readonly RotateTransform _spin = new();
    private readonly TranslateTransform _spinnerAt = new();
    private readonly ScaleTransform _spinnerSize = new(1, 1);
    private readonly Canvas _body;
    private readonly Path _shape, _ghost;
    private readonly Canvas _spinner;
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
        Width = (Anchor + 26 + Gap + Stroke) * size;
        Height = (Anchor + 26 + Gap + Stroke) * size;
        Left = -1000;
        Top = -1000;

        // Contour fin et gris très foncé, légèrement transparent : net sans être dur.
        var outline = new SolidColorBrush(Color.FromArgb(0xE6, 0x28, 0x2A, 0x30));
        outline.Freeze();
        Path Outlined(ScaleTransform scale) => new()
        {
            Data = Arrow, Fill = Brushes.White, Stroke = outline, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = scale,
        };
        _ghost = Outlined(_ghostScale);
        _ghost.Opacity = 0;
        _shape = Outlined(_morph);

        // Roue d'attente : un arc qui tourne (seule pour « occupé », à côté de la flèche pour « démarrage en cours »).
        const string arc = "M 0,-8 A 8,8 0 1 1 -8,0";
        _spinner = new Canvas
        {
            Opacity = 0,
            RenderTransform = new TransformGroup { Children = { _spin, _spinnerSize, _spinnerAt } },
            Children =
            {
                new Path { Data = Geometry.Parse(arc), Stroke = outline, StrokeThickness = 3.2 + 2 * Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
                new Path { Data = Geometry.Parse(arc), Stroke = Brushes.White, StrokeThickness = 3.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            },
        };

        _body = new Canvas
        {
            Opacity = 0,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6 * size, ShadowDepth = 1.5 * size, Opacity = 0.45 },
            // Grandit, rétrécit et s'enfonce depuis le point visé : il ne bouge jamais.
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(size, size), _scale, _press } },
            Children = { _ghost, _shape, _spinner },
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
            var grow = new DoubleAnimation(0.55, 1, Anim.Ms(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            _morph.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _morph.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _shape.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(140)) { EasingFunction = Anim.Out });
        }
        UpdateSpinner();
    }

    /// <summary>Roue d'attente : seule et centrée pour « occupé », petite à côté de la flèche pour « démarrage en cours ».</summary>
    private void UpdateSpinner()
    {
        var waiting = _current is PointerShape.Wait or PointerShape.Busy;
        if (waiting)
        {
            var small = _current == PointerShape.Busy;
            _spinnerSize.ScaleX = _spinnerSize.ScaleY = small ? 0.6 : 1;
            _spinnerAt.X = small ? 18 : 0;
            _spinnerAt.Y = small ? 20 : 0;
            if (_spin.HasAnimatedProperties == false)
                _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Anim.Ms(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        var fade = new DoubleAnimation(waiting ? 1 : 0, Anim.Ms(160)) { EasingFunction = Anim.Out };
        if (!waiting) fade.Completed += (_, _) =>
        {
            if (_current is not (PointerShape.Wait or PointerShape.Busy)) _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        _spinner.BeginAnimation(OpacityProperty, fade);
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
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
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
