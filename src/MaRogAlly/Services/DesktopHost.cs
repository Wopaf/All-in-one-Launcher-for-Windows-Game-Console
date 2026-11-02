using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MaRogAlly.Services;

/// <summary>
/// Fenêtre sans bordure, sur tout l'écran (Windows range sa barre des tâches derrière une fenêtre plein écran au premier plan).
/// Quand une autre application passe au premier plan, elle reste juste derrière elle : pas de renvoi tout au fond,
/// où d'autres fenêtres viendraient s'intercaler.
/// Pas de « toujours au-dessus » : un jeu qui démarre sans prendre le premier plan resterait caché derrière.
/// </summary>
public sealed class DesktopHost
{
    private readonly Window _window;
    private IntPtr _hwnd;

    /// <summary>Taille forcée en pixels (option « --size 1280x672 ») pour simuler un autre écran ; sinon la zone de travail.</summary>
    public (int Width, int Height)? ForcedSize { get; set; }

    public DesktopHost(Window window)
    {
        _window = window;
        window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            FitToScreen();
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) window.Dispatcher.BeginInvoke(FitToScreen);
        };
    }

    public IntPtr Handle => _hwnd;
    public bool IsForeground => _hwnd != IntPtr.Zero && GetForegroundWindow() == _hwnd;

    /// <summary>Place la fenêtre sur tout le moniteur où elle se trouve (pixels physiques).</summary>
    public void FitToScreen()
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var r = info.rcMonitor;
        var (w, h) = ForcedSize ?? (r.Right - r.Left, r.Bottom - r.Top);
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void SendToBack()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void BringToFront()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        // Windows ne cède le premier plan qu'à l'application qui a reçu la dernière saisie : touche sans effet (F24).
        keybd_event(VK_F24, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        _window.Activate();
        SetForegroundWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
            case WM_SETTINGCHANGE:
                _window.Dispatcher.BeginInvoke(FitToScreen);
                break;
        }
        return IntPtr.Zero;
    }

    private const int WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_DPICHANGED = 0x02E0;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const byte VK_F24 = 0x87;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
