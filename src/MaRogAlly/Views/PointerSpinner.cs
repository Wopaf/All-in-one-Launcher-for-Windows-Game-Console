using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace MaRogAlly.Views;

/// <summary>
/// Animation d'attente : une comète effilée (tête ronde et pleine, queue qui s'affine jusqu'à une pointe) tourne sur un
/// anneau discret. Sa longueur respire : la tête s'élance, puis la queue la rattrape, chaque mouvement avec une
/// accélération et un freinage doux, pendant que l'ensemble tourne régulièrement. Dessinée à chaque image, centrée sur
/// le milieu de l'élément (sur son origine s'il n'a pas de taille).
/// </summary>
internal sealed class PointerSpinner : FrameworkElement
{
    /// <summary>Rayon de l'anneau et épaisseur de la tête de la comète (unités du dessin du curseur).</summary>
    public const double Radius = 9, Thickness = 3.6;
    /// <summary>Épaisseur de l'anneau, bien plus fin que la comète qui glisse dessus.</summary>
    private const double Track = 1.4;
    /// <summary>Durée d'une respiration (la tête s'élance puis la queue rattrape), en secondes.</summary>
    private const double Cycle = 1.5;
    /// <summary>Angle parcouru à chaque respiration, longueur minimale de la comète, rotation de fond (degrés par seconde).</summary>
    private const double Sweep = 260, MinLength = 34, Spin = 200;
    /// <summary>Contour sombre, tracé uniquement à l'extérieur : tout le blanc de la comète reste visible, jusqu'à sa pointe.</summary>
    private const double Outline = 0.8;

    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(PointerOverlay.OutlineColor), 2 * Outline) { LineJoin = PenLineJoin.Round });
    private static readonly Pen TrackPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x28, 0x2A, 0x30)), Track + 2 * Outline));
    private static readonly Pen TrackLight = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), Track));

    private readonly Stopwatch _clock = new();
    private bool _running, _hooked;

    public PointerSpinner()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Hook();
    }

    /// <summary>Anime (vrai) ou fige (faux) la comète ; à l'arrêt, plus aucun calcul.</summary>
    public bool Running
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            if (value) _clock.Restart();
            Hook();
        }
    }

    private void Hook()
    {
        var wanted = _running && IsLoaded;
        if (wanted == _hooked) return;
        _hooked = wanted;
        if (wanted) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        // Anneau : une piste sombre et un liseré clair, visibles sur fond clair comme sombre, sans voler la vedette.
        dc.DrawEllipse(null, TrackPen, center, Radius, Radius);
        dc.DrawEllipse(null, TrackLight, center, Radius, Radius);

        var (tail, head) = Angles(_clock.Elapsed.TotalSeconds);
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        var comet = Comet(tail, head);
        dc.DrawGeometry(null, OutlinePen, comet);
        dc.DrawGeometry(Brushes.White, null, comet);
        dc.Pop();
    }

    /// <summary>Angles de la queue et de la tête (degrés, sens des aiguilles d'une montre) à cet instant.</summary>
    private static (double Tail, double Head) Angles(double t)
    {
        var n = Math.Floor(t / Cycle);
        var p = t / Cycle - n;
        // La tête part la première, la queue la rattrape ; les deux mouvements se chevauchent un peu, pour la fluidité.
        var head = n * Sweep + Ease(p / 0.58) * Sweep + MinLength;
        var tail = n * Sweep + Ease((p - 0.42) / 0.58) * Sweep;
        var turn = t * Spin - 90; // part du haut
        return (tail + turn, head + turn);
    }

    /// <summary>Accélération et freinage doux (quintique), bornés entre 0 et 1.</summary>
    private static double Ease(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x < 0.5 ? 16 * Math.Pow(x, 5) : 1 - Math.Pow(-2 * x + 2, 5) / 2;
    }

    /// <summary>
    /// Contour de la comète : bord extérieur de la queue à la tête, bout arrondi devant, bord intérieur au retour, petite
    /// pointe arrondie derrière. L'épaisseur grandit vite depuis la queue puis reste pleine vers la tête.
    /// </summary>
    private static Geometry Comet(double tailDeg, double headDeg)
    {
        var length = headDeg - tailDeg;
        var steps = Math.Max(10, (int)(length / 5));
        var outer = new Point[steps + 1];
        var inner = new Point[steps + 1];
        var halves = new double[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            var a = (tailDeg + length * u) * Math.PI / 180;
            var half = Thickness / 2 * (0.18 + 0.82 * (1 - Math.Pow(1 - u, 2.4)));
            var (cos, sin) = (Math.Cos(a), Math.Sin(a));
            outer[i] = new Point((Radius + half) * cos, (Radius + half) * sin);
            inner[i] = new Point((Radius - half) * cos, (Radius - half) * sin);
            halves[i] = half;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(outer[0], true, true);
            g.PolyLineTo(outer[1..], true, true);
            g.ArcTo(inner[steps], new Size(halves[steps], halves[steps]), 0, false, SweepDirection.Clockwise, true, true);
            g.PolyLineTo(Enumerable.Reverse(inner[..steps]).ToArray(), true, true);
            g.ArcTo(outer[0], new Size(halves[0], halves[0]), 0, false, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
