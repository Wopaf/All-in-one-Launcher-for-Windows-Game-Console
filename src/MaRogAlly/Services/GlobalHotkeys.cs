using System.Runtime.InteropServices;
using System.Windows.Interop;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>
/// Touches écoutées dans tout Windows, quelle que soit l'application au premier plan : un bouton de la machine (M1)
/// y est relié dans Armoury Crate. Plusieurs touches sans autre usage, car la liste proposée par Armoury Crate n'est pas documentée.
/// F24 est écartée : elle sert à obtenir le premier plan (voir DesktopHost).
/// </summary>
public sealed class GlobalHotkeys
{
    private const int WM_HOTKEY = 0x0312, FirstId = 0xB001;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly (uint Key, string Name)[] Keys = [(0x91, "Arrêt défil"), (0x13, "Pause"), (0x7C, "F13")];

    public event Action? Pressed;

    public GlobalHotkeys(IntPtr hwnd)
    {
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        for (var i = 0; i < Keys.Length; i++)
            if (!RegisterHotKey(hwnd, FirstId + i, MOD_NOREPEAT, Keys[i].Key))
                Log.Info($"Touche {Keys[i].Name} déjà prise par une autre application");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() is >= FirstId and var id && id < FirstId + Keys.Length)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
}
