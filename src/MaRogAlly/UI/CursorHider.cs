using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace MaRogAlly.UI;

/// <summary>
/// Cache le curseur sur les fenêtres de l'application quand elle passe au premier plan ou que la manette sert, et le rend dès que la souris bouge.
/// Windows ne permet pas de le cacher proprement sur les fenêtres des autres applications.
/// </summary>
public static class CursorHider
{
    /// <summary>Un écart plus petit n'est pas un geste : la mise en page signale aussi des mouvements, curseur immobile.</summary>
    private const int MoveThreshold = 3;

    private static Point32 _hiddenAt;

    /// <summary>La fenêtre cache le curseur à son activation, et le rend quand la souris bouge au-dessus d'elle.</summary>
    public static void Attach(Window window)
    {
        window.Activated += (_, _) => Hide();
        window.PreviewMouseMove += (_, e) =>
        {
            // Un doigt sur l'écran déplace aussi le curseur, mais Windows ne l'affiche pas pour autant.
            if (e.StylusDevice is null) ShowIfMoved();
        };
    }

    public static void Hide()
    {
        if (Mouse.OverrideCursor == Cursors.None) return;
        GetCursorPos(out _hiddenAt);
        Mouse.OverrideCursor = Cursors.None;
    }

    private static void ShowIfMoved()
    {
        if (Mouse.OverrideCursor != Cursors.None || !GetCursorPos(out var now)) return;
        if (Math.Abs(now.X - _hiddenAt.X) < MoveThreshold && Math.Abs(now.Y - _hiddenAt.Y) < MoveThreshold) return;
        Mouse.OverrideCursor = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point32 point);
}
