using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>
/// Touche écoutée dans tout Windows, quelle que soit l'application au premier plan : un bouton de la machine (M1)
/// y est relié dans Armoury Crate. Choisie dans les réglages ; par défaut, plusieurs touches sans autre usage.
/// F24 est écartée : elle sert à obtenir le premier plan (voir DesktopHost).
/// </summary>
public sealed class GlobalHotkeys
{
    private const int WM_HOTKEY = 0x0312, FirstId = 0xB001;
    private const uint MOD_NOREPEAT = 0x4000;
    public const int ScrollLock = 0x91, Pause = 0x13, F13 = 0x7C, F24 = 0x87;
    private static readonly int[] Defaults = [ScrollLock, Pause, F13];

    private readonly IntPtr _hwnd;
    private int _registered, _taken;

    /// <summary>Au moins une touche est réservée : le centre de contrôle peut s'ouvrir.</summary>
    public bool IsListening => _taken > 0;

    public event Action? Pressed;

    /// <summary>
    /// Touches proposées dans les réglages : aucune ne sert à taper ni à naviguer, et Armoury Crate sait les envoyer
    /// (il ne propose pas F14 à F24). Une autre touche reste possible avec la détection.
    /// </summary>
    public static IEnumerable<int> Choices => [ScrollLock, Pause, F13];

    public GlobalHotkeys(IntPtr hwnd, int? key)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Apply(key);
    }

    /// <summary>Écoute cette touche (null : celles par défaut). Faux si aucune n'a pu être réservée.</summary>
    public bool Apply(int? key)
    {
        Suspend();
        int[] keys = key is { } chosen ? [chosen] : Defaults;
        var taken = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (RegisterHotKey(_hwnd, FirstId + i, MOD_NOREPEAT, (uint)keys[i])) taken++;
            else Log.Info($"Touche {Name(keys[i])} déjà prise par une autre application");
        }
        _registered = keys.Length;
        _taken = taken;
        Log.Info($"Centre de contrôle : touche {Describe(key)}{(taken == 0 ? " (non réservée)" : "")}");
        return taken > 0;
    }

    /// <summary>Libère les touches : pendant le choix d'une nouvelle touche, elle doit arriver à la fenêtre.</summary>
    public void Suspend()
    {
        for (var i = 0; i < _registered; i++) UnregisterHotKey(_hwnd, FirstId + i);
        _registered = 0;
        _taken = 0;
    }

    public static string Describe(int? key) => key is { } k ? Name(k) : "Arrêt défil, Pause ou F13";

    public static string Name(int key) => key switch
    {
        ScrollLock => "Arrêt défil",
        Pause => "Pause",
        >= F13 and <= F24 => $"F{key - F13 + 13}",
        _ => KeyInterop.KeyFromVirtualKey(key).ToString(),
    };

    /// <summary>Faux pour une touche qui sert à taper ou à naviguer, ou réservée par l'application : la prendre partout gênerait.</summary>
    public static bool CanUse(Key key) => key switch
    {
        >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 => false,
        Key.Space or Key.Enter or Key.Escape or Key.Tab or Key.Back or Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down => false,
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System => false,
        Key.F24 or Key.None or Key.ImeProcessed or Key.DeadCharProcessed => false,
        _ => KeyInterop.VirtualKeyFromKey(key) != 0,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() is >= FirstId and var id && id < FirstId + _registered)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
