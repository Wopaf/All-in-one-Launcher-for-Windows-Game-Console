using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Microsoft.Win32;

namespace MaRogAlly.Views;

/// <summary>
/// Fenêtre du centre de contrôle : à gauche de l'écran, par-dessus l'application au premier plan (un jeu, le plus souvent).
/// Elle prend le premier plan, pour que le jeu cesse en principe de lire la manette, le lui rend en se fermant,
/// et se ferme dès qu'une autre fenêtre passe devant.
/// </summary>
public sealed class QuickPanelWindow : Window
{
    private const double PanelWidth = 480, ScreenGap = 14;
    /// <summary>Place laissée autour du volet pour son ombre, et assombrissement du reste de l'écran.</summary>
    private const double ShadowSpace = 40, DimOpacity = 0.5;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    /// <summary>Durée pendant laquelle on redemande le premier plan, puis nombre de relevés (100 ms) sans lui avant de fermer.</summary>
    private const int Insist = 1500, LostTicks = 3;
    /// <summary>Le volet arrive vite puis se pose, et repart plus vite encore : le jeu ne doit pas attendre.</summary>
    private const int OpenMs = 280, CloseMs = 190;
    private static readonly IEasingFunction OpenEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction CloseEase = new CubicEase { EasingMode = EasingMode.EaseIn };

    private readonly TaskCompletionSource _gone = new();
    private readonly IntPtr _previous = GetForegroundWindow();
    private readonly double _uiScale;
    private readonly bool _visible;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Stopwatch _opened = new();
    private bool _closing, _finished, _gotForeground, _slidIn;
    private int _lostFor;
    private DimWindow? _dim;
    private double _dimOpacity;

    public QuickPanel Panel { get; }

    /// <summary>Vrai dès que la fermeture commence : le premier plan est déjà rendu, le volet ne doit plus réagir.</summary>
    public bool IsClosing => _closing;

    /// <summary>Terminée quand la fenêtre a réellement disparu de l'écran.</summary>
    public Task Gone => _gone.Task;

    /// <summary>Faux quand l'action choisie mène ailleurs : l'application d'avant ne repasse pas devant.</summary>
    public bool RestorePrevious { get; set; } = true;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    /// <summary>Le volet, ou ce qu'il a ouvert en plein écran, est devant.</summary>
    public bool IsForeground => GetForegroundWindow() is var front && (front == Handle || (Stage is { } stage && front == stage.Handle));

    /// <summary>Fenêtre plein écran au-dessus du volet, tant qu'elle est ouverte.</summary>
    public QuickStageWindow? Stage { get; private set; }

    /// <summary>La fenêtre plein écran vient d'être créée : le clavier physique doit y être branché.</summary>
    public event Action<QuickStageWindow>? StageCreated;

    /// <summary>La fermeture commence, avant que l'application d'avant ne reprenne le premier plan (un jeu en pause doit être relancé d'abord).</summary>
    public event Action? CloseStarted;

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
        // Affiché d'abord sans activation : le premier plan est demandé ensuite, et redemandé si Windows ou le jeu le refuse.
        ShowActivated = false;
        // Transparente autour du volet : ses coins arrondis laissent voir le jeu, avec des bords nets.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        // Mêmes réglages de texte que la fenêtre principale : les vues en héritent.
        Foreground = (Brush)FindResource("TextPrimary");
        FontFamily = (FontFamily)FindResource("Body");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.Manual;
        panel.LayoutTransform = new ScaleTransform(uiScale, uiScale);
        // La marge reste transparente : l'ombre du volet s'y dessine.
        panel.Margin = new Thickness(Shadow);
        Content = panel;
        Place();

        // Ni dans Alt+Tab, ni dans la barre de gauche.
        SourceInitialized += (_, _) => SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
        panel.CloseRequested += CloseOnce;
        _watch.Tick += (_, _) => Watch();

        // La résolution peut changer depuis l'onglet Affichage.
        EventHandler replace = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Place();
            _dim?.Fit();
        });
        SystemEvents.DisplaySettingsChanged += replace;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= replace;
            _watch.Stop();
            _dim?.Close();
            panel.Shutdown();
            _gone.TrySetResult();
        };
    }

    /// <summary>Affiche le volet hors de l'écran, prend le premier plan, puis le fait glisser une fois dessiné : la première image, la plus lente, ne saccade pas.</summary>
    public void Open()
    {
        if (!_visible)
        {
            Show();
            return;
        }
        Left = -Width;
        ContentRendered += (_, _) => SlideIn();
        // Le voile s'affiche d'abord : propriétaire du volet, il reste toujours dessous.
        _dim = new DimWindow();
        _dim.Clicked += CloseOnce;
        _dim.Show();
        Owner = _dim;
        Show();
        _opened = Stopwatch.StartNew();
        DesktopHost.TakeForeground(this, Handle);
        _watch.Start();
    }

    /// <summary>Ouvre un panneau en plein écran, au-dessus du volet.</summary>
    public void ShowOnStage(OverlayBase overlay)
    {
        if (Stage is null)
        {
            var stage = new QuickStageWindow(this, _uiScale, _visible);
            stage.Closed += (_, _) => Stage = null;
            stage.Emptied += () => { if (!_closing) Activate(); };
            Stage = stage;
            StageCreated?.Invoke(stage);
            stage.Show();
        }
        else if (_visible) Stage.Activate();
        Stage.Open(overlay);
    }

    private void SlideIn()
    {
        if (_slidIn || _closing) return;
        _slidIn = true;
        Log.Info($"Centre de contrôle : glissement lancé après {_opened.ElapsedMilliseconds} ms");
        BeginAnimation(LeftProperty, new DoubleAnimation(-Width, Gap - Shadow, Anim.Ms(OpenMs)) { EasingFunction = OpenEase });
        Panel.PlayEntrance();
        FadeDim(DimOpacity, OpenMs);
    }

    /// <summary>Le voile suit le volet : il s'assombrit à l'ouverture et s'efface à la fermeture.</summary>
    private void FadeDim(double to, double ms)
    {
        if (_dim is not { } dim) return;
        Anim.Tween(Panel, "dim", _dimOpacity, to, ms, value =>
        {
            _dimOpacity = value;
            dim.SetOpacity(value);
        });
    }

    /// <summary>
    /// À l'ouverture, un jeu peut reprendre le premier plan : on insiste un moment, sans jamais fermer. Si Windows le refuse encore,
    /// le volet reste visible et se pilote à la manette. Ensuite seulement, un autre programme durablement devant ferme le volet.
    /// </summary>
    private void Watch()
    {
        // Si le premier rendu n'est pas signalé, le volet ne doit surtout pas rester hors de l'écran.
        if (!_slidIn && _opened.ElapsedMilliseconds > 400) SlideIn();
        if (IsForeground)
        {
            if (!_gotForeground) Log.Info($"Centre de contrôle : premier plan obtenu en {_opened.ElapsedMilliseconds} ms (position {Left:0}, {ActualWidth:0}×{ActualHeight:0}, visible {IsVisible}, glissement lancé {_slidIn})");
            _gotForeground = true;
            _lostFor = 0;
            return;
        }
        if (_opened.ElapsedMilliseconds < Insist)
        {
            DesktopHost.TakeForeground(this, Handle);
            return;
        }
        if (!_gotForeground)
        {
            Log.Info($"Centre de contrôle : premier plan refusé, volet laissé ouvert (devant : {WindowsShell.ClassOf(GetForegroundWindow())})");
            _watch.Stop();
            return;
        }
        if (++_lostFor < LostTicks) return;
        Log.Info($"Centre de contrôle : fermé, une autre fenêtre est passée devant ({WindowsShell.ClassOf(GetForegroundWindow())})");
        CloseOnce();
    }

    private void Place()
    {
        Width = PanelWidth * _uiScale + 2 * Shadow;
        Height = SystemParameters.PrimaryScreenHeight - 2 * Gap + 2 * Shadow;
        Left = _visible ? Gap - Shadow : -(Width + 10000);
        Top = Gap - Shadow;
    }

    /// <summary>Espace laissé entre le volet et les bords de l'écran.</summary>
    private double Gap => ScreenGap * _uiScale;

    /// <summary>Marge transparente autour du volet, où se dessine son ombre.</summary>
    private double Shadow => ShadowSpace * _uiScale;

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();
        CloseStarted?.Invoke();
        // Le jeu reprend la main dès le début du glissement, sans attendre sa fin.
        if (RestorePrevious && IsForeground && IsWindow(_previous)) SetForegroundWindow(_previous);
        Stage?.Close();
        if (!_slidIn)
        {
            Finish();
            return;
        }
        IsHitTestVisible = false;
        var slide = new DoubleAnimation(-Width, Anim.Ms(CloseMs)) { EasingFunction = CloseEase };
        slide.Completed += (_, _) => Finish();
        BeginAnimation(LeftProperty, slide);
        Panel.PlayExit();
        FadeDim(0, CloseMs);
        // Une animation peut ne jamais finir si le rendu s'arrête : le volet doit se fermer quand même.
        _ = Task.Delay(CloseMs + 300).ContinueWith(_ => Dispatcher.BeginInvoke(Finish));
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Close();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}

/// <summary>
/// Voile noir sur tout l'écran, sous le volet. Il n'est redessiné que pendant son fondu : ensuite, son image ne change plus.
/// Un clic dessus referme le volet.
/// </summary>
internal sealed class DimWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    public event Action? Clicked;

    public DimWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // Une opacité réglée par Windows sur une fenêtre ordinaire la laisse opaque : la transparence passe par WPF.
        AllowsTransparency = true;
        Background = Brushes.Black;
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Fit();
        // Sans jamais prendre le premier plan ni apparaître dans Alt+Tab.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    public void Fit()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public void SetOpacity(double opacity) => Opacity = Math.Clamp(opacity, 0, 1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
