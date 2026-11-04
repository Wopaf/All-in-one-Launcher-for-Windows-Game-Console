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
        Content = panel;
        Place();

        // Ni dans Alt+Tab, ni dans la barre de gauche.
        SourceInitialized += (_, _) => SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
        panel.CloseRequested += CloseOnce;
        _watch.Tick += (_, _) => Watch();

        // La résolution peut changer depuis l'onglet Affichage.
        EventHandler replace = (_, _) => Dispatcher.BeginInvoke(Place);
        SystemEvents.DisplaySettingsChanged += replace;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= replace;
            _watch.Stop();
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
        BeginAnimation(LeftProperty, new DoubleAnimation(-Width, Gap, Anim.Ms(OpenMs)) { EasingFunction = OpenEase });
        Panel.PlayEntrance();
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
        Width = PanelWidth * _uiScale;
        Height = SystemParameters.PrimaryScreenHeight - 2 * Gap;
        Left = _visible ? Gap : -(Width + 10000);
        Top = Gap;
    }

    /// <summary>Espace laissé entre le volet et les bords de l'écran.</summary>
    private double Gap => ScreenGap * _uiScale;

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();
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
