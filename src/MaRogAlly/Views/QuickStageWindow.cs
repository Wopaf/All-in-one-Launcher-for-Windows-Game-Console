using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaRogAlly.UI;
using Microsoft.Win32;

namespace MaRogAlly.Views;

/// <summary>
/// Fenêtre plein écran et transparente, posée au-dessus du centre de contrôle : elle accueille ce qui ne tient pas dans le volet
/// (image agrandie, Wi-Fi, Bluetooth, clavier). La fenêtre principale reste derrière le jeu, ses panneaux n'y seraient pas visibles.
/// </summary>
public sealed class QuickStageWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    private readonly bool _interactive;
    private readonly List<OverlayBase> _overlays = [];
    private readonly Grid _host = new(), _keyboardHost = new();
    private readonly StackPanel _hints = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18) };
    private readonly TextBlock _toastIcon = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _toastText = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _toast;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _closed;

    /// <summary>Le dernier panneau vient d'être refermé : le volet reprend la main pendant que celui-ci s'efface.</summary>
    public event Action? Emptied;

    public OverlayBase? Current => _overlays.LastOrDefault();
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <param name="interactive">Faux pour une capture de vérification : ni focus, ni au premier plan, et hors de l'écran.</param>
    public QuickStageWindow(Window owner, double uiScale, bool interactive)
    {
        _interactive = interactive;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = interactive;
        ShowActivated = interactive;
        Foreground = (Brush)FindResource("TextPrimary");
        FontFamily = (FontFamily)FindResource("Body");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        WindowStartupLocation = WindowStartupLocation.Manual;

        _toastIcon.FontFamily = (FontFamily)FindResource("Icons");
        _toast = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(18, 10, 18, 10),
            CornerRadius = new CornerRadius(14),
            Background = (Brush)FindResource("PanelBg"),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _toastIcon, _toastText } },
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Anim.Fade(_toast, 0, 250);
        };

        var root = new Grid { LayoutTransform = new ScaleTransform(uiScale, uiScale) };
        root.Children.Add(_host);
        root.Children.Add(_hints);
        root.Children.Add(_keyboardHost);
        root.Children.Add(_toast);
        Content = root;
        Place();

        // Ni dans Alt+Tab, ni dans la barre de gauche.
        SourceInitialized += (_, _) => SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
        EventHandler replace = (_, _) => Dispatcher.BeginInvoke(Place);
        SystemEvents.DisplaySettingsChanged += replace;
        Closed += (_, _) =>
        {
            _closed = true;
            SystemEvents.DisplaySettingsChanged -= replace;
            _toastTimer.Stop();
            // Chaque panneau arrête ainsi ses minuteries.
            foreach (var overlay in Enumerable.Reverse(_overlays).ToList()) overlay.Close();
        };
    }

    private void Place()
    {
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = _interactive ? 0 : -(Width + 10000);
        Top = 0;
    }

    public void Open(OverlayBase overlay)
    {
        overlay.CloseRequested += Remove;
        overlay.HintsChanged += ShowHints;
        _overlays.Add(overlay);
        if (overlay is SidePanel side) side.CenterOnWholeWindow();
        if (overlay is KeyboardPanel keyboard)
        {
            keyboard.ToastRequested += ShowToast;
            _keyboardHost.Children.Add(overlay);
        }
        else _host.Children.Add(overlay);
        if (_interactive) overlay.OnOpened();
        ShowHints();
    }

    private void Remove(OverlayBase overlay)
    {
        if (!_overlays.Remove(overlay)) return;
        overlay.IsHitTestVisible = false;
        overlay.CloseRequested -= Remove;
        overlay.HintsChanged -= ShowHints;
        Anim.Exit(overlay, 0, 0, 180, () =>
        {
            (overlay.Parent as Panel)?.Children.Remove(overlay);
            if (_overlays.Count == 0 && !_closed) Close();
        });
        ShowHints();
        if (_closed) return;
        if (_overlays.Count > 0) _overlays[^1].OnRevealed();
        else Emptied?.Invoke();
    }

    private void ShowHints()
    {
        _hints.Children.Clear();
        foreach (var part in (Current?.Hints ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator < 0) continue;
            _hints.Children.Add(new Border { Style = (Style)FindResource("PadGlyph"), Child = new TextBlock { Text = part[..separator], Style = (Style)FindResource("PadGlyphText") } });
            _hints.Children.Add(new TextBlock { Text = part[(separator + 1)..], FontSize = 14, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) });
        }
    }

    public void ShowToast(string icon, string text)
    {
        _toastIcon.Text = icon;
        _toastText.Text = text;
        Anim.Enter(_toast, 0, -16, 300);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
