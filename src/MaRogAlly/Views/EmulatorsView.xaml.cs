using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Onglet Émulateurs : un émulateur par console, à installer, mettre à jour ou désinstaller depuis l'application.
/// Liste à gauche, fiche de l'émulateur sélectionné à droite (état, actions, jeux détectés, dossiers de jeux).
/// </summary>
public partial class EmulatorsView : UserControl
{
    private HttpClient? _http;
    private AppSettings _settings = new();
    private List<Game> _games = [];
    private EmulatorInfo _current = Emulators.All[0];

    /// <summary>Dernière version publiée de chaque émulateur (null : GitHub n'a pas répondu).</summary>
    private readonly Dictionary<string, EmulatorRelease?> _latest = [];
    private DateTime _checkedAt = DateTime.MinValue;
    private bool _checking;
    /// <summary>Téléchargements en cours : avancement de 0 à 1.</summary>
    private readonly Dictionary<string, double> _busy = [];
    /// <summary>Désinstallation demandée une première fois : un second appui dans les 4 s confirme.</summary>
    private (string Id, DateTime At)? _confirmUninstall;

    private readonly Dictionary<string, (Ellipse Dot, TextBlock Text)> _rowStates = [];
    private List<EmulatorInfo> _shownList = [];
    private SlideHighlight? _highlight;

    /// <summary>Un émulateur ou un dossier de jeux a changé : la bibliothèque doit être relue.</summary>
    public event Action? LibraryChanged;
    /// <summary>Message bref (icône, texte) affiché par la fenêtre principale.</summary>
    public event Action<string, string>? Toast;

    public static readonly DependencyProperty CoverWidthProperty = DependencyProperty.Register(nameof(CoverWidth), typeof(double), typeof(EmulatorsView), new PropertyMetadata(100.0));
    public static readonly DependencyProperty CoverHeightProperty = DependencyProperty.Register(nameof(CoverHeight), typeof(double), typeof(EmulatorsView), new PropertyMetadata(140.0));
    public double CoverWidth { get => (double)GetValue(CoverWidthProperty); set => SetValue(CoverWidthProperty, value); }
    public double CoverHeight { get => (double)GetValue(CoverHeightProperty); set => SetValue(CoverHeightProperty, value); }

    public EmulatorsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyCompact(ActualHeight < 760);
        BuildList();
        Show(_current, animate: false);
    }

    public void Init(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
        // À l'ouverture : le premier émulateur installé, sinon le premier de la liste.
        _current = Emulators.All.FirstOrDefault(Emulators.IsInstalled) ?? Emulators.All[0];
        BuildList();
        Refresh();
    }

    public string Hints => FocusNav.IsFocusInside(Showcase) ? "A:Valider|B:Retour" : "A:Choisir|B:Retour";

    /// <summary>Jeux de la bibliothèque, pour compter et montrer ceux de chaque console.</summary>
    public void SetGames(IReadOnlyList<Game> games)
    {
        _games = games.Where(g => g.PlatformInfo.IsEmulated).ToList();
        Refresh();
    }

    private void ApplyCompact(bool compact)
    {
        ListTitle.FontSize = compact ? 28 : 34;
        Showcase.Margin = new Thickness(48, compact ? 14 : 30, 0, 40);
        BigLogo.Height = compact ? 50 : 72;
        BigLogo.Width = BigLogo.Height * 1.7;
        NameText.FontSize = compact ? 34 : 50;
        NameText.Margin = new Thickness(0, compact ? 8 : 18, 0, 0);
        EmulatorText.FontSize = compact ? 16 : 19;
        GamesLabel.Margin = new Thickness(0, compact ? 16 : 26, 0, compact ? 8 : 10);
        CoverHeight = compact ? 100 : 150;
        CoverWidth = Math.Floor(CoverHeight / CoverArt.Ratio);
    }

    // ---------------- Liste ----------------

    private void BuildList()
    {
        List.Children.Clear();
        _rowStates.Clear();
        // Les émulateurs que l'application installe, et ceux déjà présents sur la machine (yuzu).
        _shownList = Emulators.Visible.ToList();
        foreach (var e in _shownList) List.Children.Add(BuildRow(e));
    }

    private Button BuildRow(EmulatorInfo e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse(e.PlatformInfo.Logo), Fill = Brushes.White, Stretch = Stretch.Uniform,
            Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = e.PlatformInfo.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        var text = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(dot);
        state.Children.Add(text);
        name.Children.Add(state);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        _rowStates[e.Id] = (dot, text);

        var count = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15,
            Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Tag = "count",
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        var row = new Button { Content = grid, Tag = e, Style = (Style)FindResource("RowButton") };
        RowOptions.SetNoBar(row, true);
        RowOptions.SetFloating(row, true);
        RowOptions.SetCard(row, true);
        row.Margin = new Thickness(0, 4, 0, 4);
        row.Click += (_, _) => FocusActions();
        row.GotKeyboardFocus += (_, _) =>
        {
            _highlight ??= new SlideHighlight(ListHighlight, ListHost);
            _highlight.MoveTo(row);
            Show(e, animate: true);
        };
        return row;
    }

    private Button? RowOf(EmulatorInfo e) => List.Children.OfType<Button>().FirstOrDefault(b => b.Tag == e);

    // ---------------- États ----------------

    private enum State { NotInstalled, Downloading, Installed, UpdateAvailable }

    private State StateOf(EmulatorInfo e, out EmulatorInstall? installed)
    {
        installed = Emulators.Installed(e);
        if (_busy.ContainsKey(e.Id)) return State.Downloading;
        if (installed is null) return State.NotInstalled;
        return _latest.GetValueOrDefault(e.Id) is { } latest && Emulators.IsNewer(latest, installed) ? State.UpdateAvailable : State.Installed;
    }

    private static readonly Color Green = Color.FromRgb(0x6B, 0xD3, 0x6B), Orange = Color.FromRgb(0xFF, 0x9F, 0x1C),
        Grey = Color.FromRgb(0x6B, 0x74, 0x84), Blue = Color.FromRgb(0x4D, 0xA3, 0xFF);

    private (Color Color, string Text) Describe(EmulatorInfo e)
    {
        var state = StateOf(e, out var installed);
        return state switch
        {
            State.Downloading => (Blue, $"Téléchargement… {_busy[e.Id]:P0}"),
            State.NotInstalled => (Grey, "Non installé"),
            State.UpdateAvailable => (Orange, $"Mise à jour disponible ({_latest[e.Id]!.Version})"),
            _ when !e.Downloadable => (Green, installed!.Version == "?" ? "Déjà installé sur la machine" : $"Déjà installé · version {installed.Version}"),
            _ => (Green, installed!.Version == "?" ? "Installé" : $"Installé · version {installed.Version}"),
        };
    }

    private List<Game> GamesOf(EmulatorInfo e) => _games.Where(g => g.Platform == e.Platform).ToList();

    /// <summary>Met à jour la liste et la fiche (états, compteurs), sans perdre la sélection.</summary>
    private void Refresh()
    {
        // Un émulateur repris est apparu ou a disparu de la machine : la liste suit.
        if (!Emulators.Visible.SequenceEqual(_shownList))
        {
            BuildList();
            if (!_shownList.Contains(_current)) _current = _shownList[0];
        }
        foreach (var e in Emulators.All)
        {
            if (!_rowStates.TryGetValue(e.Id, out var row)) continue;
            var (color, text) = Describe(e);
            row.Dot.Fill = new SolidColorBrush(color);
            row.Text.Text = text;
            if (RowOf(e)?.Content is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "count")) is { } count)
                count.Text = GamesOf(e).Count is var n and > 0 ? n.ToString() : "";
        }
        var installed = _shownList.Count(Emulators.IsInstalled);
        Summary.Text = $"{installed} sur {_shownList.Count} installés · {_games.Count} jeu{(_games.Count > 1 ? "x" : "")}";
        Show(_current, animate: false);
    }

    // ---------------- Fiche ----------------

    private void Show(EmulatorInfo e, bool animate)
    {
        var changed = _current != e;
        _current = e;
        var info = e.PlatformInfo;
        var color = (Color)ColorConverter.ConvertFromString(info.Color);

        BigLogo.Data = Geometry.Parse(info.Logo);
        Watermark.Data = BigLogo.Data;
        LogoGlow.Color = color;
        NameText.Text = info.Name;
        EmulatorText.Text = $"Émulateur : {e.Name}";
        PitchText.Text = e.Pitch;

        var state = StateOf(e, out var installed);
        var (dot, text) = Describe(e);
        StatusDot.Fill = new SolidColorBrush(dot);
        StatusText.Text = text;
        ProgressBar.Visibility = state == State.Downloading ? Visibility.Visible : Visibility.Collapsed;
        if (state == State.Downloading) ProgressFill.Width = ProgressBar.Width * _busy[e.Id];

        BuildActions(e, state);
        NoteText.Text = installed is null
            ? "Téléchargé depuis la page officielle du projet, puis rangé dans le dossier Emulators de l'application."
            : e.FirstRunNote;

        var games = GamesOf(e);
        Covers.ItemsSource = games.OrderByDescending(g => g.LastPlayedAny ?? DateTime.MinValue).ThenBy(g => g.DisplayTitle).Take(8).ToList();
        GamesLabel.Text = games.Count switch
        {
            0 => "JEUX",
            1 => "1 JEU DÉTECTÉ",
            var n => $"{n} JEUX DÉTECTÉS",
        };
        NoGamesText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGamesText.Text = installed is null
            ? "Installe l'émulateur : les jeux de ses dossiers apparaîtront dans Mes Jeux."
            : $"Aucun jeu trouvé. Dépose tes fichiers de jeux ({string.Join(", ", e.Extensions)}) dans un des dossiers ci-dessous.";
        BuildFolders(e);

        GlowStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x5C, color.R, color.G, color.B), Anim.Ms(700)) { EasingFunction = Anim.Out });
        GlowEnd.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0, color.R, color.G, color.B), Anim.Ms(700)) { EasingFunction = Anim.Out });
        if (animate && changed) Anim.Enter(Showcase, 0, 18, 360);
    }

    private void BuildActions(EmulatorInfo e, State state)
    {
        var focusedKey = FocusNav.IsFocusInside(Actions) ? (Keyboard.FocusedElement as Button)?.Tag as string : null;
        Actions.Children.Clear();
        switch (state)
        {
            case State.NotInstalled:
                Actions.Children.Add(Action("install", "", "Installer", () => _ = InstallAsync(e, update: false)));
                break;
            case State.Downloading:
                Actions.Children.Add(Action("wait", "", "Téléchargement en cours…", () => { }, enabled: false));
                break;
            default:
                Actions.Children.Add(Action("launch", "", $"Ouvrir {e.Name}", () => Launch(e)));
                // Émulateur repris : ni mise à jour ni désinstallation depuis l'application.
                if (!e.Downloadable) break;
                Actions.Children.Add(state == State.UpdateAvailable
                    ? Action("update", "", "Mettre à jour", () => _ = InstallAsync(e, update: true))
                    : Action("check", "", _checking ? "Recherche…" : "Rechercher une mise à jour", () => _ = CheckUpdatesAsync(force: true)));
                Actions.Children.Add(Action("uninstall", "",
                    _confirmUninstall is { } c && c.Id == e.Id && DateTime.Now - c.At < TimeSpan.FromSeconds(4) ? "Confirmer la désinstallation" : "Désinstaller",
                    () => Uninstall(e)));
                break;
        }
        // Le bouton qui avait le focus est recréé : le focus le retrouve (sinon le premier bouton actif).
        if (focusedKey is not null && (Actions.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, focusedKey)) ?? Actions.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled)) is { } again)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(again), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BuildFolders(EmulatorInfo e)
    {
        var focusedKey = FocusNav.IsFocusInside(Folders) ? (Keyboard.FocusedElement as Button)?.Tag as string : null;
        Folders.Children.Clear();
        var extra = _settings.RomFolders.GetValueOrDefault(e.Platform) ?? [];
        foreach (var folder in Emulators.RomFolders(e, _settings))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var count = GamesOf(e).Count(g => g.LaunchArgs?.Contains(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) == true);
            var missing = !Directory.Exists(folder) && folder != e.RomDir;
            row.Children.Add(Action($"open:{folder}", "", $"{folder}  ·  {(missing ? "introuvable" : count == 1 ? "1 jeu" : $"{count} jeux")}", () => OpenFolder(e, folder), small: true));
            if (extra.Contains(folder, StringComparer.OrdinalIgnoreCase))
                row.Children.Add(Action($"remove:{folder}", "", "Retirer", () => RemoveFolder(e, folder), small: true));
            Folders.Children.Add(row);
        }
        Folders.Children.Add(Action("add", "", "Ajouter un dossier de jeux", () => AddFolder(e), small: true));
        if (focusedKey is not null && Folders.Children.OfType<FrameworkElement>()
                .SelectMany(c => c is Panel p ? p.Children.OfType<Button>() : c is Button b ? [b] : Enumerable.Empty<Button>())
                .FirstOrDefault(b => Equals(b.Tag, focusedKey)) is { } again)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(again), System.Windows.Threading.DispatcherPriority.Input);
    }

    private Button Action(string key, string glyph, string label, Action onClick, bool enabled = true, bool small = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = small ? 14 : 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 640 });
        var button = new Button
        {
            Style = (Style)FindResource("PillButton"), Content = content, Tag = key, IsEnabled = enabled,
            Height = small ? 40 : 48, Padding = new Thickness(small ? 18 : 24, 0, small ? 18 : 24, 0), FontSize = small ? 14 : 16,
            Margin = new Thickness(0, 0, 12, small ? 0 : 10), Opacity = enabled ? 1 : 0.6, HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------- Actions ----------------

    private async Task InstallAsync(EmulatorInfo e, bool update)
    {
        if (_http is null || _busy.ContainsKey(e.Id)) return;
        if (update && Emulators.IsRunning(e))
        {
            Toast?.Invoke("", $"Ferme {e.Name} avant de le mettre à jour");
            return;
        }
        _busy[e.Id] = 0;
        Refresh();
        try
        {
            var release = _latest.GetValueOrDefault(e.Id) ?? await Emulators.LatestAsync(_http, e, CancellationToken.None);
            if (release is null)
            {
                Toast?.Invoke("", $"Impossible de trouver {e.Name} sur GitHub pour l'instant");
                return;
            }
            _latest[e.Id] = release;
            var last = DateTime.MinValue;
            var progress = new Progress<double>(p =>
            {
                _busy[e.Id] = p;
                // Affichage rafraîchi quelques fois par seconde : la fiche reste fluide.
                if (DateTime.Now - last < TimeSpan.FromMilliseconds(150) && p < 1) return;
                last = DateTime.Now;
                UpdateProgress(e);
            });
            await Emulators.InstallAsync(_http, e, release, progress, CancellationToken.None);
            Toast?.Invoke("", update ? $"{e.Name} est à jour ({release.Version})" : $"{e.Name} est installé");
        }
        catch (Exception ex)
        {
            Log.Error($"Installation de {e.Name}", ex);
            Toast?.Invoke("", ex is InvalidDataException ? ex.Message : $"L'installation de {e.Name} a échoué (voir log.txt)");
        }
        finally
        {
            _busy.Remove(e.Id);
            Refresh();
            LibraryChanged?.Invoke();
        }
    }

    private void UpdateProgress(EmulatorInfo e)
    {
        var (color, text) = Describe(e);
        if (_rowStates.TryGetValue(e.Id, out var row)) row.Text.Text = text;
        if (_current != e) return;
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        ProgressFill.Width = ProgressBar.Width * _busy.GetValueOrDefault(e.Id);
    }

    private void Launch(EmulatorInfo e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.ExePath) { WorkingDirectory = e.Dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Ouverture de {e.Name}", ex);
            Toast?.Invoke("", $"Impossible d'ouvrir {e.Name}");
        }
    }

    private void Uninstall(EmulatorInfo e)
    {
        // Premier appui : le bouton demande confirmation (les réglages et sauvegardes rangés dans l'émulateur partent avec lui).
        if (_confirmUninstall is not { } c || c.Id != e.Id || DateTime.Now - c.At > TimeSpan.FromSeconds(4))
        {
            _confirmUninstall = (e.Id, DateTime.Now);
            Show(e, animate: false);
            Toast?.Invoke("", $"Ses réglages et sauvegardes seront supprimés. Appuie encore pour confirmer.");
            return;
        }
        _confirmUninstall = null;
        if (Emulators.IsRunning(e))
        {
            Toast?.Invoke("", $"Ferme {e.Name} avant de le désinstaller");
            return;
        }
        try
        {
            Emulators.Uninstall(e);
            Toast?.Invoke("", $"{e.Name} a été désinstallé (tes jeux sont gardés)");
        }
        catch (Exception ex)
        {
            Log.Error($"Désinstallation de {e.Name}", ex);
            Toast?.Invoke("", $"La désinstallation de {e.Name} a échoué (voir log.txt)");
        }
        Refresh();
        if (RowOf(e) is { } row) FocusNav.Focus(row);
        LibraryChanged?.Invoke();
    }

    private void OpenFolder(EmulatorInfo e, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Ouverture du dossier {folder}", ex);
            Toast?.Invoke("", "Impossible d'ouvrir ce dossier");
        }
    }

    private void AddFolder(EmulatorInfo e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Dossier de jeux {e.PlatformInfo.Name}" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dialog.FolderName;
        if (!_settings.RomFolders.TryGetValue(e.Platform, out var list)) _settings.RomFolders[e.Platform] = list = [];
        if (Emulators.RomFolders(e, _settings).Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(folder);
        _settings.Save();
        Show(e, animate: false);
        LibraryChanged?.Invoke();
    }

    private void RemoveFolder(EmulatorInfo e, string folder)
    {
        if (_settings.RomFolders.TryGetValue(e.Platform, out var list))
        {
            list.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) _settings.RomFolders.Remove(e.Platform);
            _settings.Save();
        }
        Show(e, animate: false);
        if (Folders.Children.OfType<Button>().LastOrDefault() is { } add) FocusNav.Focus(add);
        LibraryChanged?.Invoke();
    }

    /// <summary>Dernières versions publiées : à l'affichage de l'onglet (au plus une fois par heure), ou sur demande.</summary>
    public async Task CheckUpdatesAsync(bool force = false)
    {
        if (_http is null || _checking || !force && DateTime.Now - _checkedAt < TimeSpan.FromHours(1)) return;
        _checking = true;
        Show(_current, animate: false);
        try
        {
            // Seuls ceux que l'application installe ont une version à suivre sur GitHub.
            var found = await Task.WhenAll(Emulators.All.Select(e => e.Downloadable ? Emulators.LatestAsync(_http, e, CancellationToken.None) : Task.FromResult<EmulatorRelease?>(null)));
            for (var i = 0; i < found.Length; i++)
                if (found[i] is { } release) _latest[Emulators.All[i].Id] = release;
            _checkedAt = DateTime.Now;
            if (force && Emulators.Installed(_current) is { } installed)
                Toast?.Invoke("", _latest.GetValueOrDefault(_current.Id) is { } latest
                    ? Emulators.IsNewer(latest, installed) ? $"Version {latest.Version} disponible" : $"{_current.Name} est à jour"
                    : "GitHub ne répond pas pour l'instant");
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    // ---------------- Navigation ----------------

    public void FocusFirst()
    {
        if (IsKeyboardFocusWithin) return;
        if ((RowOf(_current) ?? List.Children.OfType<Button>().FirstOrDefault()) is { } row)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(row), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusActions()
    {
        if (Actions.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled) is { } first) FocusNav.Focus(first);
        else if (Folders.Children.OfType<Button>().FirstOrDefault() is { } add) FocusNav.Focus(add);
    }

    public bool Handle(NavAction action)
    {
        if (FocusNav.IsFocusInside(Showcase))
        {
            switch (action)
            {
                case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                    // Au bord de la fiche (à gauche ou en haut) : retour à la liste.
                    if (!FocusNav.Move(action, Showcase) && action is NavAction.Left or NavAction.Up && RowOf(_current) is { } row) FocusNav.Focus(row);
                    return true;
                case NavAction.Accept:
                    FocusNav.Activate();
                    return true;
                case NavAction.Back:
                    if (RowOf(_current) is { } back) FocusNav.Focus(back);
                    return true;
            }
            return false;
        }
        switch (action)
        {
            case NavAction.Up or NavAction.Down:
                FocusNav.Move(action, List);
                return true;
            case NavAction.Right or NavAction.Accept:
                if (!List.IsKeyboardFocusWithin) FocusFirst();
                else FocusActions();
                return true;
        }
        return false;
    }
}
