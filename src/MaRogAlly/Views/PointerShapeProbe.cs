using System.Runtime.InteropServices;
using System.Windows.Automation;
using MaRogAlly.Core;

namespace MaRogAlly.Views;

/// <summary>
/// Forme du curseur de la souris à la manette, relevée sur un fil à part (rien ne ralentit le mouvement).
/// <list type="bullet">
/// <item>Windows d'abord : quand il choisit une forme particulière (texte, lien, redimensionnement, attente…), elle est
/// reprise telle quelle. Sans souris branchée, il cache son curseur et ne la donne souvent plus.</item>
/// <item>Sinon, la forme est devinée : bord d'une fenêtre redimensionnable (flèches), application figée (attente),
/// champ de texte (barre), bouton, lien, onglet, élément de liste ou de menu (main).</item>
/// </list>
/// </summary>
public sealed class PointerShapeProbe
{
    /// <summary>Intervalle entre deux relevés quand le curseur bouge, et quand il est immobile (une page qui charge…).</summary>
    private const int MovingMs = 70, StillMs = 400;

    private readonly Func<bool> _active;
    private readonly Action<PointerShape> _changed;
    private readonly Func<IntPtr, int, int, PointerShape>? _own;
    private PointerShape _last = PointerShape.Arrow;

    private static readonly ControlType[] Clickable =
    [
        ControlType.Button, ControlType.Hyperlink, ControlType.MenuItem, ControlType.TabItem, ControlType.CheckBox,
        ControlType.RadioButton, ControlType.ComboBox, ControlType.SplitButton, ControlType.ListItem, ControlType.TreeItem,
    ];

    /// <param name="active">Vrai quand le curseur de l'application est affiché.</param>
    /// <param name="changed">Nouvelle forme (appelé depuis le fil du relevé).</param>
    /// <param name="own">Forme dans une fenêtre de l'application (fenêtre, point de l'écran) : ses éléments sont lus directement.</param>
    public PointerShapeProbe(Func<bool> active, Action<PointerShape> changed, Func<IntPtr, int, int, PointerShape>? own = null)
    {
        _active = active;
        _changed = changed;
        _own = own;
        new Thread(Loop) { IsBackground = true, Name = "Forme du curseur", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void Loop()
    {
        int lastX = int.MinValue, lastY = int.MinValue;
        var lastProbe = 0L;
        while (true)
        {
            Thread.Sleep(MovingMs);
            if (!_active())
            {
                // Curseur caché : il réapparaîtra en flèche, comme le veut notre fenêtre à sa création.
                lastX = int.MinValue;
                continue;
            }
            if (!GetCursorPos(out var at)) continue;
            var now = Environment.TickCount64;
            var moved = at.X != lastX || at.Y != lastY;
            if (!moved && now - lastProbe < StillMs) continue;
            (lastX, lastY, lastProbe) = (at.X, at.Y, now);
            try
            {
                var shape = ShapeAt(at);
                if (shape == _last) continue;
                _last = shape;
                _changed(shape);
            }
            catch (Exception ex)
            {
                Log.Error("Forme du curseur", ex);
                Thread.Sleep(1000);
            }
        }
    }

    private PointerShape ShapeAt(Point32 at)
    {
        // Windows sait mieux que nous quand il donne une forme particulière.
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref info) && info.Cursor != IntPtr.Zero && PointerOverlay.ShapeOf(info.Cursor) is var windows and not PointerShape.Arrow)
            return windows;

        var hwnd = WindowFromPoint(at);
        if (hwnd == IntPtr.Zero) return PointerShape.Arrow;
        var root = GetAncestor(hwnd, GaRoot);
        if (root != IntPtr.Zero && IsHungAppWindow(root)) return PointerShape.Wait;
        if (ResizeEdge(hwnd, at) is { } edge) return edge;
        if (_own is not null && root != IntPtr.Zero && GetWindowThreadProcessId(root, out var pid) != 0 && pid == Environment.ProcessId)
            return _own(root, at.X, at.Y);
        return FromAutomation(at);
    }

    /// <summary>Bord d'une fenêtre redimensionnable : la fenêtre dit elle-même quelle partie est sous le curseur.</summary>
    private static PointerShape? ResizeEdge(IntPtr hwnd, Point32 at)
    {
        var point = (IntPtr)((at.X & 0xFFFF) | ((at.Y & 0xFFFF) << 16));
        if (SendMessageTimeout(hwnd, WmNcHitTest, IntPtr.Zero, point, SmtoAbortIfHung, 60, out var hit) == IntPtr.Zero) return null;
        return (int)hit switch
        {
            10 or 11 => PointerShape.SizeWE,     // gauche, droite
            12 or 15 => PointerShape.SizeNS,     // haut, bas
            13 or 17 => PointerShape.SizeNWSE,   // haut gauche, bas droite
            14 or 16 => PointerShape.SizeNESW,   // haut droite, bas gauche
            _ => null,
        };
    }

    /// <summary>
    /// Élément sous le curseur (accessibilité de Windows) : champ de texte, ou élément cliquable. On remonte de quelques
    /// niveaux, le texte d'un bouton ou d'un lien étant souvent l'élément le plus profond.
    /// </summary>
    private static PointerShape FromAutomation(Point32 at)
    {
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(new System.Windows.Point(at.X, at.Y)); }
        catch { return PointerShape.Arrow; }
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < 4; depth++)
        {
            try
            {
                var type = element.Current.ControlType;
                if (type == ControlType.Edit) return PointerShape.Text;
                if (type == ControlType.Document) return IsEditable(element) ? PointerShape.Text : PointerShape.Arrow;
                if (Clickable.Contains(type)) return element.Current.IsEnabled ? PointerShape.Hand : PointerShape.Arrow;
                // Au-delà d'une fenêtre ou d'un panneau, on ne survole plus un bouton.
                if (type == ControlType.Window || type == ControlType.Pane || type == ControlType.List || type == ControlType.Tree) break;
                element = walker.GetParent(element);
            }
            catch (ElementNotAvailableException) { break; }
        }
        return PointerShape.Arrow;
    }

    /// <summary>Zone de texte modifiable (un document en lecture seule, comme une page web, garde la flèche).</summary>
    internal static bool IsEditable(AutomationElement element)
        => element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly;

    private const int GaRoot = 2;
    private const uint WmNcHitTest = 0x84, SmtoAbortIfHung = 0x2;

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
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point32 point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
