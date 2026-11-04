using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MaRogAlly.Views;

/// <summary>
/// Fenêtre du centre de contrôle : à gauche de l'écran, par-dessus l'application au premier plan (un jeu, le plus souvent).
/// Elle prend le premier plan, pour que le jeu cesse en principe de lire la manette, le lui rend en se fermant,
/// et se ferme dès qu'une autre fenêtre passe devant.
/// </summary>
public sealed class QuickPanelWindow : Window
{
    private const double PanelWidth = 480;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    private readonly IntPtr _previous = GetForegroundWindow();
    private readonly double _uiScale;
    private readonly bool _visible;
    private bool _closing;

    public QuickPanel Panel { get; }

    /// <summary>Faux quand l'action choisie mène ailleurs : l'application d'avant ne repasse pas devant.</summary>
    public bool RestorePrevious { get; set; } = true;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public bool IsForeground => GetForegroundWindow() == Handle;

    /// <param name="activate">Faux pour une capture de vérification : ni focus, ni au premier plan, et hors de l'écran.</param>
    public QuickPanelWindow(QuickPanel panel, double uiScale, bool activate)
    {
        Panel = panel;
        _uiScale = uiScale;
        _visible = activate;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = activate;
        ShowActivated = activate;
        // Mêmes réglages de texte que la fenêtre principale : les vues en héritent.
        Background = (Brush)FindResource("Bg");
        Foreground = (Brush)FindResource("TextPrimary");
        FontFamily = (FontFamily)FindResource("Body");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.Manual;
        panel.LayoutTransform = new ScaleTransform(uiScale, uiScale);
        Content = panel;
        Place();

        // Ni dans Alt+Tab, ni dans la barre de gauche.
        SourceInitialized += (_, _) => SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
        panel.CloseRequested += CloseOnce;
        if (activate) Deactivated += (_, _) => Dispatcher.BeginInvoke(CloseOnce);

        // La résolution peut changer depuis l'onglet Affichage.
        EventHandler replace = (_, _) => Dispatcher.BeginInvoke(Place);
        SystemEvents.DisplaySettingsChanged += replace;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= replace;
            panel.Shutdown();
        };
    }

    private void Place()
    {
        Width = PanelWidth * _uiScale;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = _visible ? 0 : -(Width + 10000);
        Top = 0;
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        var giveBack = RestorePrevious && IsForeground && IsWindow(_previous);
        Close();
        if (giveBack) SetForegroundWindow(_previous);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
