using System.Runtime.InteropServices;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>
/// Masquage automatique de la barre des tâches de Windows (le réglage standard, pas une fenêtre cachée de force) :
/// elle reste accessible depuis le bord de l'écran, et le réglage d'origine revient à la fermeture.
/// </summary>
public static class WindowsTaskbar
{
    public static void Apply(AppSettings settings)
    {
        if (!settings.HideWindowsTaskbar)
        {
            Restore(settings);
            return;
        }
        if (settings.TaskbarAutoHideBefore is null)
        {
            settings.TaskbarAutoHideBefore = IsAutoHide;
            settings.Save();
        }
        IsAutoHide = true;
    }

    public static void Restore(AppSettings settings)
    {
        if (settings.TaskbarAutoHideBefore is not { } before) return;
        IsAutoHide = before;
        settings.TaskbarAutoHideBefore = null;
        settings.Save();
    }

    private static bool IsAutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            return (SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64() & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), lParam = new IntPtr(value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP) };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
    }

    private const uint ABM_GETSTATE = 0x04, ABM_SETSTATE = 0x0A;
    private const int ABS_AUTOHIDE = 0x01, ABS_ALWAYSONTOP = 0x02;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}
