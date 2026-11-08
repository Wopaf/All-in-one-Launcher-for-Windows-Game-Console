using System.Diagnostics;
using System.Runtime.InteropServices;
using MaRogAlly.Core;

namespace MaRogAlly.Input;

/// <summary>État de la manette relevé quand l'application n'est pas au premier plan.</summary>
public readonly record struct PadState(ushort Buttons, short LeftX, short LeftY, short RightX, short RightY);

/// <summary>
/// Souris à la manette sur les applications de bureau, pour se passer du mode bureau de la ROG Ally (où la manette n'est
/// plus lue comme une manette) : stick droit pour le curseur, RB clic gauche (maintenu pour glisser), LB clic droit,
/// stick gauche pour défiler. Sans souris branchée, Windows cache son curseur : l'application affiche alors le sien
/// (<see cref="ShowPointer"/>) à la position du curseur de Windows, qui continue de bouger pour les clics et le survol.
/// </summary>
public sealed class PadMouse
{
    /// <summary>Vitesse du curseur, stick poussé au bout (pixels par seconde).</summary>
    private const double MaxSpeed = 1500;
    /// <summary>Défilement, stick poussé au bout (crans de molette par seconde).</summary>
    private const double MaxScroll = 14;
    private const double DeadZone = 0.18;
    /// <summary>Le curseur s'efface après ce temps sans rien toucher (une vidéo regardée, par exemple).</summary>
    private const double IdleSeconds = 4;
    private const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800, HWheel = 0x01000;
    private const int Notch = 120, CursorShowing = 1;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last, _lastInput, _restX, _restY, _scrollY, _scrollX;
    private bool _left, _right, _active, _pointerShown;

    /// <summary>La souris ne s'active que si cette condition est vraie (application de bureau au premier plan).</summary>
    public Func<bool> IsWanted { get; set; } = () => false;

    /// <summary>Affiche le curseur de l'application à cette position (pixels de l'écran).</summary>
    public Action<int, int>? ShowPointer { get; set; }

    public Action? HidePointer { get; set; }

    public void Update(PadState s)
    {
        var now = _clock.Elapsed.TotalSeconds;
        // Après une pause (relevés arrêtés), on ne rattrape pas le temps perdu d'un coup.
        var dt = Math.Min(now - _last, 0.05);
        _last = now;
        if (!IsWanted())
        {
            if (_active) Log.Info("Souris à la manette : inactive");
            _active = false;
            Release();
            return;
        }
        if (!_active)
        {
            _active = true;
            _lastInput = now;
            Log.Info("Souris à la manette : active");
        }

        var moveX = Curve(s.RightX);
        var moveY = Curve(s.RightY);
        var scrollX = Curve(s.LeftX);
        var scrollY = Curve(s.LeftY);
        if (moveX != 0 || moveY != 0 || scrollX != 0 || scrollY != 0 || (s.Buttons & (LeftShoulder | RightShoulder)) != 0) _lastInput = now;

        var dx = moveX * MaxSpeed * dt + _restX;
        var dy = -moveY * MaxSpeed * dt + _restY;
        var mx = (int)Math.Truncate(dx);
        var my = (int)Math.Truncate(dy);
        _restX = dx - mx;
        _restY = dy - my;
        if ((mx != 0 || my != 0) && GetCursorPos(out var at)) SetCursorPos(at.X + mx, at.Y + my);

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

    /// <summary>Notre curseur suit celui de Windows, sauf si Windows montre déjà le sien (souris branchée) ou après un moment sans rien toucher.</summary>
    private void UpdatePointer(double now)
    {
        var visible = now - _lastInput < IdleSeconds && !WindowsCursorShown() && GetCursorPos(out var at);
        if (visible)
        {
            GetCursorPos(out at);
            ShowPointer?.Invoke(at.X, at.Y);
            _pointerShown = true;
        }
        else if (_pointerShown)
        {
            HidePointer?.Invoke();
            _pointerShown = false;
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
        _restX = _restY = _scrollX = _scrollY = 0;
        if (_pointerShown) HidePointer?.Invoke();
        _pointerShown = false;
    }

    private static void SetButton(ref bool held, bool pressed, uint down, uint up)
    {
        if (held == pressed) return;
        held = pressed;
        mouse_event(pressed ? down : up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Zone morte, puis courbe : précis près du centre, rapide au bout.</summary>
    private static double Curve(short value)
    {
        var n = value / 32767.0;
        var a = Math.Abs(n);
        if (a < DeadZone) return 0;
        var t = Math.Min(1, (a - DeadZone) / (1 - DeadZone));
        return Math.Sign(n) * t * t;
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
}
