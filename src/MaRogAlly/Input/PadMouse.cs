using System.Diagnostics;
using System.Runtime.InteropServices;
using MaRogAlly.Core;

namespace MaRogAlly.Input;

/// <summary>État de la manette relevé quand l'application n'est pas au premier plan.</summary>
public readonly record struct PadState(ushort Buttons, short LeftX, short LeftY, short RightX, short RightY);

/// <summary>Choix du mode souris (réglages) : taille du curseur, vitesse et accélération.</summary>
public static class PointerOptions
{
    public static readonly (double Value, string Label)[] Sizes = [(0.8, "Petite"), (1.0, "Normale"), (1.25, "Grande")];
    public static readonly (double Value, string Label)[] Speeds = [(0.6, "Lente"), (1.0, "Normale"), (1.5, "Rapide"), (2.2, "Très rapide")];
    /// <summary>Exposant de la courbe du stick : 1, la vitesse suit le stick ; plus haut, précis au centre et rapide au bout.</summary>
    public static readonly (double Value, string Label)[] Accelerations = [(1.0, "Aucune"), (1.5, "Faible"), (2.0, "Normale"), (2.8, "Forte")];

    public static string Label((double Value, string Label)[] options, double value) => options.MinBy(o => Math.Abs(o.Value - value)).Label;

    /// <summary>Choix proposé le plus proche d'une valeur enregistrée (une taille qui n'est plus proposée devient la plus proche).</summary>
    public static double Nearest((double Value, string Label)[] options, double value) => options.MinBy(o => Math.Abs(o.Value - value)).Value;
}

/// <summary>
/// Souris à la manette, pour se passer du mode bureau de la ROG Ally (où la manette n'est plus lue comme une manette) :
/// stick droit pour le curseur, RB clic gauche (maintenu pour glisser), LB clic droit, stick gauche pour défiler.
/// Le curseur est déplacé à chaque image affichée par l'écran (120 fois par seconde sur un écran à 120 Hz), par un fil
/// à part, avec un léger lissage ; notre flèche est posée au même endroit dans la même boucle, sans décalage.
/// Sans souris branchée, Windows cache son curseur : la flèche de l'application le remplace.
/// </summary>
public sealed class PadMouse
{
    /// <summary>Vitesse du curseur, stick poussé au bout, à la vitesse « normale » (pixels par seconde).</summary>
    private const double MaxSpeed = 1500;
    /// <summary>Défilement, stick poussé au bout (crans de molette par seconde).</summary>
    private const double MaxScroll = 14;
    private const double DeadZone = 0.18;
    /// <summary>Lissage du mouvement : la vitesse rejoint celle du stick en quelques centièmes de seconde.</summary>
    private const double Smoothing = 0.03;
    /// <summary>Le curseur s'efface après ce temps sans rien toucher (une vidéo regardée, par exemple).</summary>
    private const double IdleSeconds = 4;
    private const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800, HWheel = 0x01000;
    private const int Notch = 120, CursorShowing = 1;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private PadState _state;
    private double _last, _lastInput, _scrollY, _scrollX;
    private bool _left, _right, _active;
    private volatile bool _pointerShown, _moving;
    private Thread? _mover;

    /// <summary>La souris ne s'active que si cette condition est vraie (application de bureau au premier plan).</summary>
    public Func<bool> IsWanted { get; set; } = () => false;

    /// <summary>Multiplicateur de vitesse (1 : normale).</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Exposant de la courbe du stick (voir <see cref="PointerOptions.Accelerations"/>).</summary>
    public double Acceleration { get; set; } = 2;

    /// <summary>Affiche le curseur de l'application à cette position (pixels de l'écran), sur le fil de l'interface.</summary>
    public Action<int, int>? ShowPointer { get; set; }

    public Action? HidePointer { get; set; }

    /// <summary>Clic enfoncé (vrai) ou relâché (faux) : animation du curseur de l'application.</summary>
    public Action<bool>? PressPointer { get; set; }

    /// <summary>Remet le curseur de l'application tout devant, depuis le fil qui déplace la souris.</summary>
    public Action? RaisePointer { get; set; }

    /// <summary>
    /// Forme choisie par Windows sous le curseur (handle du curseur système), depuis le fil qui déplace la souris :
    /// main sur un lien, barre sur du texte, flèches sur un bord à redimensionner…
    /// </summary>
    public Action<IntPtr>? ShapePointer { get; set; }

    /// <summary>Déplace le curseur de l'application, depuis le fil qui déplace la souris.</summary>
    public Action<int, int>? MovePointer { get; set; }

    /// <summary>À chaque relevé de la manette (fil de l'interface) : activation, clics, défilement, apparition du curseur.</summary>
    public void Update(PadState s)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _last, 0.05);
        _last = now;
        lock (_gate) _state = s;
        if (!IsWanted())
        {
            Deactivate("inactive");
            return;
        }
        if (!_active)
        {
            _active = true;
            _lastInput = now;
            Log.Info("Souris à la manette : active");
            StartMover();
        }

        var (scrollX, scrollY) = Stick(s.LeftX, s.LeftY, 2);
        if (Stick(s.RightX, s.RightY, 1) != (0, 0) || scrollX != 0 || scrollY != 0 || (s.Buttons & (LeftShoulder | RightShoulder)) != 0)
            _lastInput = now;

        // Stick gauche : molette, verticale et horizontale.
        _scrollY += scrollY * MaxScroll * Notch * dt;
        _scrollX += scrollX * MaxScroll * Notch * dt;
        if (Math.Abs(_scrollY) >= 1)
        {
            var amount = (int)Math.Truncate(_scrollY);
            _scrollY -= amount;
            mouse_event(Wheel, 0, 0, amount, UIntPtr.Zero);
        }
        if (Math.Abs(_scrollX) >= 1)
        {
            var amount = (int)Math.Truncate(_scrollX);
            _scrollX -= amount;
            mouse_event(HWheel, 0, 0, amount, UIntPtr.Zero);
        }

        SetButton(ref _left, (s.Buttons & RightShoulder) != 0, LeftDown, LeftUp);
        SetButton(ref _right, (s.Buttons & LeftShoulder) != 0, RightDown, RightUp);
        UpdatePointer(now);
    }

    /// <summary>
    /// Vérification indépendante de la manette (minuterie) : les relevés s'arrêtent quand la manette n'est plus lue
    /// (changement de mode) ou que l'application passe devant. Le curseur ne doit pas rester affiché pour autant.
    /// </summary>
    public void Watch()
    {
        if (!_active && !_pointerShown && !_left && !_right) return;
        var stale = _clock.Elapsed.TotalSeconds - _last > 0.3;
        if (!stale && IsWanted()) return;
        Deactivate(stale ? "manette plus lue, curseur caché" : "inactive");
    }

    private void Deactivate(string reason)
    {
        if (_active) Log.Info($"Souris à la manette : {reason}");
        _active = false;
        _moving = false;
        Release();
    }

    /// <summary>Notre curseur suit celui de Windows, sauf si Windows montre déjà le sien (souris branchée) ou après un moment sans rien toucher.</summary>
    private void UpdatePointer(double now)
    {
        if (now - _lastInput < IdleSeconds && !WindowsCursorShown() && GetCursorPos(out var at))
        {
            if (_pointerShown) return;
            _pointerShown = true;
            ShowPointer?.Invoke(at.X, at.Y);
        }
        else if (_pointerShown)
        {
            _pointerShown = false;
            HidePointer?.Invoke();
        }
    }

    /// <summary>Fil qui déplace le curseur, calé sur l'affichage de l'écran (une fois par image).</summary>
    private void StartMover()
    {
        _moving = true;
        if (_mover is { IsAlive: true }) return;
        _mover = new Thread(MoveLoop) { IsBackground = true, Name = "Souris à la manette", Priority = ThreadPriority.AboveNormal };
        _mover.Start();
    }

    private void MoveLoop()
    {
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        double vx = 0, vy = 0, restX = 0, restY = 0, raised = 0, shaped = 0;
        var shape = IntPtr.Zero;
        while (_moving)
        {
            // Attend la prochaine image de l'écran : un déplacement par image, ni plus ni moins.
            if (DwmFlush() != 0) Thread.Sleep(4);
            var now = clock.Elapsed.TotalSeconds;
            // Un menu contextuel, la barre des tâches… s'ouvrent devant tout : le curseur y repasse dix fois par seconde.
            if (_pointerShown && now - raised > 0.1)
            {
                raised = now;
                RaisePointer?.Invoke();
            }
            // La forme suit celle que Windows donne sous le curseur, même immobile (une page qui charge, un lien qui apparaît).
            if (!_pointerShown) shape = IntPtr.Zero;
            else if (now - shaped > 0.03)
            {
                shaped = now;
                var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref info) && info.Cursor != IntPtr.Zero && info.Cursor != shape)
                {
                    shape = info.Cursor;
                    ShapePointer?.Invoke(shape);
                }
            }
            var dt = Math.Min(now - last, 0.05);
            last = now;
            PadState s;
            lock (_gate) s = _state;
            var speed = MaxSpeed * Speed;
            var (stickX, stickY) = Stick(s.RightX, s.RightY, Acceleration);
            var targetX = stickX * speed;
            var targetY = -stickY * speed;
            // Lissage indépendant de la fréquence de l'écran : la vitesse rejoint celle du stick sans à-coups.
            var follow = 1 - Math.Exp(-dt / Smoothing);
            vx += (targetX - vx) * follow;
            vy += (targetY - vy) * follow;
            if (targetX == 0 && Math.Abs(vx) < 1) vx = 0;
            if (targetY == 0 && Math.Abs(vy) < 1) vy = 0;

            var dx = vx * dt + restX;
            var dy = vy * dt + restY;
            var mx = (int)Math.Truncate(dx);
            var my = (int)Math.Truncate(dy);
            restX = dx - mx;
            restY = dy - my;
            if (mx == 0 && my == 0 || !GetCursorPos(out var at)) continue;
            SetCursorPos(at.X + mx, at.Y + my);
            // Position réelle (le bord de l'écran arrête le curseur) : notre flèche s'y pose dans la même image.
            if (_pointerShown && GetCursorPos(out var placed)) MovePointer?.Invoke(placed.X, placed.Y);
        }
    }

    private static bool WindowsCursorShown()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0;
    }

    /// <summary>Relâche un clic resté enfoncé (l'application ou un jeu passe devant pendant un glisser) et cache notre curseur.</summary>
    public void Release()
    {
        SetButton(ref _left, false, LeftDown, LeftUp);
        SetButton(ref _right, false, RightDown, RightUp);
        _scrollX = _scrollY = 0;
        if (_pointerShown) HidePointer?.Invoke();
        _pointerShown = false;
    }

    private void SetButton(ref bool held, bool pressed, uint down, uint up)
    {
        if (held == pressed) return;
        held = pressed;
        mouse_event(pressed ? down : up, 0, 0, 0, UIntPtr.Zero);
        PressPointer?.Invoke(pressed);
    }

    /// <summary>
    /// Stick lu comme une direction, pas comme deux axes séparés : zone morte ronde, puis courbe appliquée à la distance
    /// au centre (avec un exposant au-dessus de 1, précis près du centre et rapide au bout). L'angle du stick est gardé tel
    /// quel : les diagonales ne sont plus attirées vers l'horizontale ou la verticale.
    /// </summary>
    private static (double X, double Y) Stick(short rawX, short rawY, double exponent)
    {
        double x = rawX / 32767.0, y = rawY / 32767.0;
        var length = Math.Sqrt(x * x + y * y);
        if (length < DeadZone) return (0, 0);
        // Un stick carré dépasse 1 en diagonale : la vitesse maximale est la même dans toutes les directions.
        var t = Math.Min(1, (length - DeadZone) / (1 - DeadZone));
        var scale = Math.Pow(t, exponent) / length;
        return (x * scale, y * scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public Point32 Position;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point32 point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
