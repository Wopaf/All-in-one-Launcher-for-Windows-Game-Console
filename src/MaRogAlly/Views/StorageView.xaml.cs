using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Gestion du stockage, disque par disque : vue d'ensemble et pistes pour libérer de la place, jeux, dossiers et gros fichiers.
/// La précédente analyse reste affichée pendant qu'une nouvelle tourne.
/// </summary>
public partial class StorageView : OverlayBase
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromHours(24);

    /// <summary>Teintes dans l'ordre validé pour fond sombre ; le système reste neutre, il n'y a rien à y faire.</summary>
    private static readonly (StorageCategory Category, string Name, string Detail, Color Color)[] Categories =
    [
        (StorageCategory.Games, "Jeux", "Jeux installés et bibliothèques Steam", Color.FromRgb(0x39, 0x87, 0xE5)),
        (StorageCategory.Apps, "Applications", "Programmes et leurs données", Color.FromRgb(0xD9, 0x59, 0x26)),
        (StorageCategory.Personal, "Fichiers personnels", "Documents, images, vidéos, musique, bureau", Color.FromRgb(0x19, 0x9E, 0x70)),
        (StorageCategory.Downloads, "Téléchargements", "Ton dossier Téléchargements", Color.FromRgb(0xC9, 0x85, 0x00)),
        (StorageCategory.Temp, "Fichiers temporaires", "Temporaires et caches de shaders", Color.FromRgb(0xD5, 0x51, 0x81)),
        (StorageCategory.RecycleBin, "Corbeille", "Fichiers supprimés, pas encore effacés", Color.FromRgb(0x00, 0x83, 0x00)),
        (StorageCategory.Other, "Autres fichiers", "Tout le reste du disque", Color.FromRgb(0x90, 0x85, 0xE9)),
        (StorageCategory.System, "Système", "Windows, et zones que seul le système peut lire", Color.FromRgb(0x6B, 0x74, 0x84)),
    ];

    private static readonly (StorageFileType Type, string Name)[] Types =
    [
        (StorageFileType.Videos, "Vidéos"), (StorageFileType.Images, "Images"), (StorageFileType.Music, "Musique"),
        (StorageFileType.Archives, "Archives et images disque"), (StorageFileType.Installers, "Programmes d'installation"),
        (StorageFileType.Documents, "Documents"), (StorageFileType.Other, "Autres"),
    ];

    private enum Page { Overview, Games, Folders, Files }

    public event Action<Game>? GameRequested;
    public event Action<OverlayBase>? OverlayRequested;
    public event Action<string, string>? ToastRequested;

    private readonly List<Game> _games;
    private readonly SlideHighlight _menuHighlight, _highlight;
    private readonly List<Button> _menuButtons = [];
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<StorageDrive> _drives = [];
    private int _drive;
    private Page _page;
    private bool _gamesByAge, _loaded;
    private Button? _lastRow;
    /// <summary>Dossiers ouverts, du disque au dossier affiché.</summary>
    private readonly List<StorageFolder> _path = [];

    /// <summary>Barre et lignes de la vue d'ensemble, mises à jour pendant une première analyse.</summary>
    private StackedBar? _stack;
    private readonly Dictionary<StorageCategory, (TextBlock Value, Action<double> Meter)> _liveRows = [];

    public StorageView(IEnumerable<Game> games)
    {
        InitializeComponent();
        _games = games.ToList();
        _menuHighlight = new SlideHighlight(MenuHighlight, MenuHost);
        _highlight = new SlideHighlight(Highlight, ItemsHost);
        _scanTimer.Tick += (_, _) => ShowScanProgress();
        StorageStore.ScanFinished += OnScanFinished;
        CloseRequested += _ =>
        {
            StorageStore.ScanFinished -= OnScanFinished;
            _scanTimer.Stop();
        };
        AddMenu(Page.Overview, "", "Vue d'ensemble");
        AddMenu(Page.Games, "", "Jeux");
        AddMenu(Page.Folders, "", "Dossiers");
        AddMenu(Page.Files, "", "Gros fichiers");
    }

    protected override FrameworkElement AnimatedPart => Root;
    protected override double EnterFromX => 0;
    protected override FrameworkElement FocusScope => Menu;

    private StorageDrive? Drive => _drives.ElementAtOrDefault(_drive);
    private StorageScan? Scan => Drive is { } d ? StorageStore.Get(d.Root) : null;
    private bool InContent => Items.IsKeyboardFocusWithin;

    public override string Hints
    {
        get
        {
            var drives = _drives.Count > 1 ? "LB:Disque|RB:Disque|" : "";
            if (!InContent) return $"A:Entrer|{drives}X:Analyser|B:Fermer";
            return _page switch
            {
                Page.Games => "A:Fiche du jeu|Y:Changer le tri|X:Analyser|B:Retour",
                Page.Folders => "A:Ouvrir|Y:Explorateur|X:Analyser|B:Retour",
                Page.Files => "A:Actions|Y:Explorateur|X:Analyser|B:Retour",
                _ => "A:Valider|X:Analyser|B:Retour",
            };
        }
    }

    public override async void OnOpened()
    {
        base.OnOpened();
        _drives = StorageDrive.Read();
        // Le disque système d'abord : c'est presque toujours lui qu'on vient regarder.
        _drive = Math.Max(0, _drives.FindIndex(d => d.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)));
        BuildDriveChips();
        Render();
        await StorageStore.LoadAsync();
        _loaded = true;
        ShowDrive();
    }

    /// <summary>Retour depuis une fiche ou un volet : la sélection reprend sa place, et la corbeille a pu changer.</summary>
    public override void OnRevealed()
    {
        if (_lastRow is { IsVisible: true } row) FocusNav.Focus(row);
        else if (_menuButtons.ElementAtOrDefault((int)_page) is { } button) FocusNav.Focus(button);
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down:
                FocusNav.Move(action, InContent ? Items : Menu);
                return true;
            case NavAction.Right when !InContent:
                EnterContent();
                return true;
            case NavAction.Left when InContent:
                BackToMenu();
                return true;
            case NavAction.Back when InContent:
                if (_page == Page.Folders && _path.Count > 1) FolderUp();
                else BackToMenu();
                return true;
            case NavAction.PrevTab:
                ChangeDrive(-1);
                return true;
            case NavAction.NextTab:
                ChangeDrive(1);
                return true;
            case NavAction.Option:
                StartScan();
                return true;
            case NavAction.Secondary when InContent:
                Secondary();
                return true;
            case NavAction.Accept or NavAction.Back:
                return base.Handle(action);
        }
        return true;
    }

    private void Secondary()
    {
        switch (_page)
        {
            case Page.Games:
                _gamesByAge = !_gamesByAge;
                Render(keepFocus: true);
                break;
            case Page.Folders when (Keyboard.FocusedElement as Button)?.Tag is StorageFolder folder:
                GameLauncher.Open(FolderPath(folder));
                break;
            case Page.Files when (Keyboard.FocusedElement as Button)?.Tag is StorageFile file:
                ShowInExplorer(file.Path);
                break;
        }
    }

    private void AddMenu(Page page, string icon, string name)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = icon, FontFamily = Res<FontFamily>("Icons"), FontSize = 19, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var chevron = new TextBlock { Text = "", FontFamily = Res<FontFamily>("Icons"), FontSize = 11, Foreground = Res<Brush>("TextMuted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var button = new Button { Content = grid, Style = Res<Style>("RowButton") };
        RowOptions.SetNoBar(button, true);
        RowOptions.SetFloating(button, true);
        button.GotKeyboardFocus += (_, _) =>
        {
            _highlight.Hide();
            _menuHighlight.MoveTo(button);
            ShowPage(page);
            RaiseHintsChanged();
        };
        button.Click += (_, _) => EnterContent();
        _menuButtons.Add(button);
        Menu.Children.Add(button);
    }

    private void ShowPage(Page page)
    {
        if (page == _page && Items.Children.Count > 0) return;
        _page = page;
        Render();
        Scroll.ScrollToTop();
        Anim.Enter(Items, 24, 0, 240);
    }

    private void EnterContent()
    {
        var rows = Items.Children.OfType<Button>().ToList();
        var target = rows.Contains(_lastRow!) ? _lastRow : rows.FirstOrDefault();
        if (target is not null) FocusNav.Focus(target);
    }

    private void BackToMenu()
    {
        _highlight.Hide();
        FocusNav.Focus(_menuButtons[(int)_page]);
    }

    private void GoTo(Page page)
    {
        _lastRow = null;
        FocusNav.Focus(_menuButtons[(int)page]);
        ShowPage(page);
        Dispatcher.BeginInvoke(EnterContent, DispatcherPriority.Loaded);
    }

    // ---------------- Disques ----------------

    private void BuildDriveChips()
    {
        DriveChips.Children.Clear();
        PrevDrive.Visibility = NextDrive.Visibility = _drives.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _drives.Count; i++)
        {
            var index = i;
            var active = i == _drive;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Background = active ? new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                Child = new TextBlock { Text = _drives[i].Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>(active ? "TextPrimary" : "TextSecondary") },
            };
            chip.MouseLeftButtonUp += (_, _) => ChangeDrive(index - _drive);
            DriveChips.Children.Add(chip);
        }
    }

    private void ChangeDrive(int delta)
    {
        var next = _drive + delta;
        if (delta == 0 || next < 0 || next >= _drives.Count) return;
        _drive = next;
        _path.Clear();
        _lastRow = null;
        BuildDriveChips();
        ShowDrive();
    }

    private void ShowDrive()
    {
        var scan = Scan;
        if (scan is null || DateTime.Now - scan.ScannedAt > ScanLifetime) StartScan();
        else if (StorageStore.Running is not null) _scanTimer.Start();
        Render(keepFocus: true);
        Scroll.ScrollToTop();
        ShowScanProgress();
    }

    private void PrevDrive_Click(object sender, MouseButtonEventArgs e) => ChangeDrive(-1);
    private void NextDrive_Click(object sender, MouseButtonEventArgs e) => ChangeDrive(1);
    private void Rescan_Click(object sender, MouseButtonEventArgs e) => StartScan();

    // =====================================================================
    // Analyse
    // =====================================================================

    private void StartScan()
    {
        if (!_loaded || Drive is not { } drive) return;
        StorageStore.Start(drive, _games);
        _scanTimer.Start();
        ShowScanProgress();
    }

    private void OnScanFinished(string root, bool done)
    {
        if (StorageStore.Running is null) _scanTimer.Stop();
        if (done && Drive?.Root == root)
        {
            _path.Clear();
            Render(keepFocus: true);
        }
        ShowScanProgress();
    }

    private void ShowScanProgress()
    {
        if (StorageStore.Running is { } scanner && Drive is { } drive && scanner.Root == drive.Root)
        {
            var ratio = drive.Used > 0 ? Math.Min(1, (double)scanner.Measured / drive.Used) : 0;
            ScanTitle.Text = $"Analyse en cours · {ratio:P0}";
            ScanDetail.Text = scanner.Current;
            ScanTrack.Visibility = Visibility.Visible;
            RescanHint.Visibility = Visibility.Collapsed;
            if (ScanTrack.ActualWidth > 0)
                ScanFill.BeginAnimation(WidthProperty, new DoubleAnimation(ScanTrack.ActualWidth * ratio, Anim.Ms(250)));

            // Première analyse de ce disque : le résultat se construit sous les yeux.
            if (Scan is null && _page == Page.Overview && _stack is not null)
            {
                var live = scanner.LiveCategories();
                _stack.Update(live);
                var largest = Math.Max(1, live.Values.Max());
                foreach (var (category, (value, meter)) in _liveRows)
                {
                    value.Text = StorageScanner.Format(live[category]);
                    meter((double)live[category] / largest);
                }
            }
            return;
        }

        ScanTrack.Visibility = Visibility.Collapsed;
        RescanHint.Visibility = Visibility.Visible;
        if (Scan is { } scan)
        {
            ScanTitle.Text = $"Analysé {Ago(scan.ScannedAt)}";
            ScanDetail.Text = $"{scan.Files.ToString("N0", Fr)} fichiers mesurés";
        }
        else
        {
            ScanTitle.Text = _loaded ? "Pas encore analysé" : "Chargement…";
            ScanDetail.Text = "";
        }
    }

    // =====================================================================
    // Contenu
    // =====================================================================

    /// <summary>Reconstruit la section affichée ; <paramref name="keepFocus"/> garde la ligne sélectionnée.</summary>
    private void Render(bool keepFocus = false)
    {
        var rows = Items.Children.OfType<Button>().ToList();
        var focused = keepFocus && InContent ? rows.IndexOf((Keyboard.FocusedElement as Button)!) : -1;
        if (InContent) FocusNav.Focus(_menuButtons[(int)_page]);
        _lastRow = null;
        Items.Children.Clear();
        _liveRows.Clear();
        _stack = null;

        if (Drive is { } drive)
        {
            Summary.Text = $"{StorageScanner.Format(drive.Free)} libres sur {StorageScanner.Format(drive.Total)}";
            switch (_page)
            {
                case Page.Overview: RenderOverview(drive); break;
                case Page.Games: RenderGames(drive); break;
                case Page.Folders: RenderFolders(drive); break;
                case Page.Files: RenderFiles(); break;
            }
        }
        else Items.Children.Add(Empty("Aucun disque à afficher."));

        if (focused < 0) return;
        var rebuilt = Items.Children.OfType<Button>().ToList();
        if (rebuilt.Count > 0)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(rebuilt[Math.Min(focused, rebuilt.Count - 1)]), DispatcherPriority.Loaded);
    }

    // ---------------- Vue d'ensemble ----------------

    private Dictionary<StorageCategory, long> CategoryBytes(StorageDrive drive, StorageScan scan)
    {
        var bytes = Enum.GetValues<StorageCategory>().ToDictionary(c => c, c => scan.Categories.GetValueOrDefault(c));
        // Ce que l'analyse n'a pas pu lire reste du système (fichiers protégés, autres sessions…).
        bytes[StorageCategory.System] += Math.Max(0, scan.Used - scan.Measured);
        bytes[StorageCategory.RecycleBin] = StorageScanner.RecycleBinSize(drive.Root);
        return bytes;
    }

    private void RenderOverview(StorageDrive drive)
    {
        var scan = Scan;
        var bytes = scan is not null ? CategoryBytes(drive, scan) : Enum.GetValues<StorageCategory>().ToDictionary(c => c, _ => 0L);
        Items.Children.Add(HeroCard(drive, bytes));

        if (scan is not null)
        {
            var tips = Tips(drive, scan, bytes);
            Items.Children.Add(Heading("Libérer de la place"));
            if (tips.Count == 0) Items.Children.Add(Empty("Rien d'évident à libérer sur ce disque pour le moment."));
            foreach (var tip in tips) Items.Children.Add(tip);
        }

        Items.Children.Add(Heading("Ce qui prend de la place"));
        var shown = Categories.Where(c => scan is null || bytes[c.Category] > 0).OrderByDescending(c => scan is null ? 0 : bytes[c.Category]).ToList();
        var largest = Math.Max(1, bytes.Values.Max());
        foreach (var (category, name, detail, color) in shown)
        {
            var row = Row(Swatch(color), name, detail, (double)bytes[category] / largest, StorageScanner.Format(bytes[category]), CategoryAction(category, drive), out var value, out var meter, bar: new SolidColorBrush(color));
            _liveRows[category] = (value!, meter!);
            Items.Children.Add(row);
        }

        if (scan is not null && scan.Types.Values.Sum() > 0)
        {
            Items.Children.Add(Heading("Par type de fichier"));
            Items.Children.Add(TypesCard(scan));
        }
        Items.Children.Add(Heading("Windows"));
        Items.Children.Add(Row(Icon(""), "Paramètres de stockage de Windows", "Assistant Stockage, fichiers temporaires, emplacement des nouveaux contenus", null, null, () => GameLauncher.Open("ms-settings:storagesense"), out _, out _));
    }

    private Border HeroCard(StorageDrive drive, Dictionary<StorageCategory, long> bytes)
    {
        var panel = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var used = new StackPanel();
        used.Children.Add(new TextBlock { Text = "UTILISÉS", Style = Res<Style>("Label") });
        used.Children.Add(new TextBlock { Text = StorageScanner.Format(drive.Used), FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.Bold, FontSize = 44, Foreground = Res<Brush>("TextPrimary") });
        head.Children.Add(used);
        var free = new StackPanel();
        free.Children.Add(new TextBlock { Text = "LIBRES", Style = Res<Style>("Label"), HorizontalAlignment = HorizontalAlignment.Right });
        free.Children.Add(new TextBlock { Text = StorageScanner.Format(drive.Free), FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.Bold, FontSize = 44, Foreground = Res<Brush>("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(free, 1);
        head.Children.Add(free);
        panel.Children.Add(head);

        _stack = new StackedBar(drive.Total);
        _stack.Element.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_stack.Element);
        _stack.Update(bytes);

        var ratio = drive.Total > 0 ? (double)drive.Free / drive.Total : 1;
        if (ratio < 0.1)
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var orange = new SolidColorBrush(UpdateBadge.Few);
            warning.Children.Add(new TextBlock { Text = "", FontFamily = Res<FontFamily>("Icons"), FontSize = 14, Foreground = orange, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            warning.Children.Add(new TextBlock { Text = $"Disque presque plein : plus que {ratio:P0} d'espace libre", FontSize = 14, Foreground = Res<Brush>("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(warning);
        }
        return new Border { CornerRadius = new CornerRadius(18), Background = Res<Brush>("SurfaceGlass"), Padding = new Thickness(24, 16, 24, 20), Child = panel };
    }

    private Border TypesCard(StorageScan scan)
    {
        var rows = Types.Select(t => (t.Name, Bytes: scan.Types.GetValueOrDefault(t.Type))).Where(t => t.Bytes > 0).OrderByDescending(t => t.Bytes).ToList();
        var largest = Math.Max(1, rows[0].Bytes);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Fichiers personnels, téléchargements et autres : hors jeux, applications et système", FontSize = 13, Foreground = Res<Brush>("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var (name, size) in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.Children.Add(new TextBlock { Text = name, FontSize = 14, Foreground = Res<Brush>("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var bar = Meter((double)size / largest, Res<Brush>("Accent"), out _);
            bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var value = new TextBlock { Text = StorageScanner.Format(size), FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Res<Brush>("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            panel.Children.Add(row);
        }
        return new Border { CornerRadius = new CornerRadius(16), Background = Res<Brush>("SurfaceGlass"), Padding = new Thickness(20, 16, 20, 10), Child = panel };
    }

    private Action? CategoryAction(StorageCategory category, StorageDrive drive) => category switch
    {
        StorageCategory.Games => () => GoTo(Page.Games),
        StorageCategory.Downloads => () => GameLauncher.Open(StorageScanner.DownloadsFolder),
        StorageCategory.Temp => () => GameLauncher.Open("ms-settings:storagesense"),
        StorageCategory.RecycleBin => () => ConfirmEmptyBin(drive),
        StorageCategory.Personal or StorageCategory.Apps or StorageCategory.Other => () => GoTo(Page.Folders),
        _ => null,
    };

    /// <summary>Pistes concrètes, de la plus rentable à la moins rentable ; seules celles qui en valent la peine sont proposées.</summary>
    private List<Button> Tips(StorageDrive drive, StorageScan scan, Dictionary<StorageCategory, long> bytes)
    {
        const long Gb = 1L << 30;
        var tips = new List<(long Gain, string Icon, string Title, string Detail, Action Run)>();
        if (bytes[StorageCategory.RecycleBin] >= 100L << 20)
            tips.Add((bytes[StorageCategory.RecycleBin], "", "Vider la corbeille", "Ces fichiers occupent encore le disque", () => ConfirmEmptyBin(drive)));
        if (bytes[StorageCategory.Temp] >= Gb)
            tips.Add((bytes[StorageCategory.Temp], "", "Nettoyer les fichiers temporaires", "Ouvre le nettoyage de Windows", () => GameLauncher.Open("ms-settings:storagesense")));
        if (bytes[StorageCategory.Downloads] >= Gb)
            tips.Add((bytes[StorageCategory.Downloads], "", "Trier les téléchargements", "Installateurs et archives y restent souvent pour rien", () => GameLauncher.Open(StorageScanner.DownloadsFolder)));
        var dormant = InstalledGames(drive).Where(g => IsDormant(g.Game)).ToList();
        var dormantBytes = dormant.Sum(g => g.Bytes ?? 0);
        if (dormantBytes >= 5 * Gb)
            tips.Add((dormantBytes, "", $"{dormant.Count} jeu{(dormant.Count > 1 ? "x" : "")} sans partie depuis 6 mois", "Tu pourras les réinstaller plus tard", () =>
            {
                _gamesByAge = true;
                GoTo(Page.Games);
            }));
        var big = scan.LargestFiles.Where(f => f.Bytes >= Gb).ToList();
        if (big.Count > 0)
            tips.Add((big.Sum(f => f.Bytes), "", $"{big.Count} fichier{(big.Count > 1 ? "s" : "")} de plus de 1 Go", "Vérifie s'ils te servent encore", () => GoTo(Page.Files)));
        return tips.OrderByDescending(t => t.Gain)
            .Select(t => Row(Icon(t.Icon), t.Title, t.Detail, null, StorageScanner.Format(t.Gain), t.Run, out _, out _)).ToList();
    }

    private void ConfirmEmptyBin(StorageDrive drive)
    {
        var size = StorageScanner.RecycleBinSize(drive.Root);
        if (size == 0)
        {
            ToastRequested?.Invoke("", "La corbeille de ce disque est déjà vide");
            return;
        }
        var confirm = new SidePanel("Vider la corbeille ?", $"{StorageScanner.Format(size)} sur {drive.Letter} seront effacés définitivement");
        confirm.SetHints("A:Valider|B:Annuler");
        confirm.AddChoice("Oui, vider la corbeille", false, () =>
        {
            confirm.Close();
            if (!StorageScanner.EmptyRecycleBin(drive.Root))
            {
                ToastRequested?.Invoke("", "Impossible de vider la corbeille");
                return;
            }
            ToastRequested?.Invoke("", $"Corbeille vidée : {StorageScanner.Format(size)} libérés");
            RefreshDriveSizes();
            Render();
        });
        confirm.AddChoice("Annuler", false, confirm.Close);
        OverlayRequested?.Invoke(confirm);
    }

    private void RefreshDriveSizes()
    {
        var root = Drive?.Root;
        var fresh = StorageDrive.Read();
        if (fresh.Find(d => d.Root == root) is { } drive) _drives[_drive] = drive;
    }

    // ---------------- Jeux ----------------

    private List<(Game Game, long? Bytes)> InstalledGames(StorageDrive drive)
    {
        var sizes = Scan?.Games;
        return _games.Where(g => g.IsInstalled && g.InstallDir is { } d && string.Equals(Path.GetPathRoot(d), drive.Root, StringComparison.OrdinalIgnoreCase))
            .GroupBy(g => StorageScanner.Norm(g.InstallDir!), StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.First(), sizes is not null && sizes.TryGetValue(group.Key, out var bytes) ? bytes : (long?)null))
            .ToList();
    }

    private static DateTime LastActivity(Game g) => g.LastPlayedAny ?? g.FirstSeen;
    private static bool IsDormant(Game g) => DateTime.Now - LastActivity(g) > TimeSpan.FromDays(180);

    private void RenderGames(StorageDrive drive)
    {
        var games = InstalledGames(drive);
        if (games.Count == 0)
        {
            Items.Children.Add(Empty($"Aucun jeu de ta bibliothèque n'est installé sur {drive.Letter}"));
            return;
        }
        var total = games.Sum(g => g.Bytes ?? 0);
        var ordered = _gamesByAge
            ? games.OrderBy(g => LastActivity(g.Game)).ToList()
            : games.OrderByDescending(g => g.Bytes ?? -1).ToList();
        var count = games.Count > 1 ? $"{games.Count} jeux" : "1 jeu";
        Items.Children.Add(Heading($"{count} · {StorageScanner.Format(total)} · {(_gamesByAge ? "les plus délaissés d'abord" : "les plus lourds d'abord")}", top: 0));

        var largest = Math.Max(1, games.Max(g => g.Bytes ?? 0));
        foreach (var (game, bytes) in ordered)
        {
            var played = game.LastPlayedAny is { } last ? $"Dernière partie {Ago(last)}" : "Jamais lancé";
            var row = Row(Cover(game), game.DisplayTitle, $"{game.PlatformInfo.Name} · {played}", bytes is { } b ? (double)b / largest : null,
                bytes is { } size ? StorageScanner.Format(size) : "–", () => GameRequested?.Invoke(game), out _, out _, height: 66);
            row.Tag = game;
            Items.Children.Add(row);
        }
    }

    // ---------------- Dossiers ----------------

    private string FolderPath(StorageFolder? child = null)
    {
        var names = _path.Skip(1).Select(f => f.Name).ToList();
        if (child is not null) names.Add(child.Name);
        return names.Count == 0 ? Drive!.Root : Path.Join(Drive!.Root, string.Join('\\', names));
    }

    private void RenderFolders(StorageDrive drive)
    {
        if (Scan is not { } scan)
        {
            Items.Children.Add(Empty("Les dossiers apparaîtront à la fin de la première analyse."));
            return;
        }
        if (_path.Count == 0) _path.Add(scan.Root);
        var current = _path[^1];
        Items.Children.Add(Heading($"{FolderPath()} · {StorageScanner.Format(current.Bytes)}", top: 0));

        var children = (current.Children ?? []).Take(60).ToList();
        foreach (var child in children)
        {
            var folder = child;
            var ratio = current.Bytes > 0 ? (double)child.Bytes / current.Bytes : 0;
            var hint = child.Children is null ? "Pas de sous-dossier important" : $"{child.Children.Count} sous-dossier{(child.Children.Count > 1 ? "s" : "")} détaillé{(child.Children.Count > 1 ? "s" : "")}";
            var row = Row(Icon(""), child.Name, $"{ratio:P0} de ce dossier · {hint}", ratio, StorageScanner.Format(child.Bytes), () => OpenFolder(folder), out _, out _);
            row.Tag = child;
            Items.Children.Add(row);
        }

        var rest = current.Bytes - children.Sum(c => c.Bytes);
        if (rest > 0) Items.Children.Add(Note($"Fichiers de ce dossier et petits sous-dossiers : {StorageScanner.Format(rest)}"));
        if (_path.Count == 1 && scan.Used > scan.Measured)
            Items.Children.Add(Note($"Non lisible sans droits administrateur (système, autres sessions) : {StorageScanner.Format(scan.Used - scan.Measured)}"));
    }

    private void OpenFolder(StorageFolder folder)
    {
        if (folder.Children is null)
        {
            GameLauncher.Open(FolderPath(folder));
            return;
        }
        _path.Add(folder);
        Render();
        Scroll.ScrollToTop();
        FocusFirstRow();
        RaiseHintsChanged();
    }

    private void FolderUp()
    {
        var left = _path[^1];
        _path.RemoveAt(_path.Count - 1);
        Render();
        Dispatcher.BeginInvoke(() =>
        {
            if (Items.Children.OfType<Button>().FirstOrDefault(b => b.Tag == left) is { } row) FocusNav.Focus(row);
            else FocusFirstRow();
        }, DispatcherPriority.Loaded);
    }

    private void FocusFirstRow() => Dispatcher.BeginInvoke(() =>
    {
        if (Items.Children.OfType<Button>().FirstOrDefault() is { } row) FocusNav.Focus(row);
    }, DispatcherPriority.Loaded);

    // ---------------- Gros fichiers ----------------

    private void RenderFiles()
    {
        if (Scan is not { } scan)
        {
            Items.Children.Add(Empty("Les fichiers apparaîtront à la fin de la première analyse."));
            return;
        }
        if (scan.LargestFiles.Count == 0)
        {
            Items.Children.Add(Empty("Aucun gros fichier en dehors des jeux et du système."));
            return;
        }
        Items.Children.Add(Heading($"Les {scan.LargestFiles.Count} plus gros · hors jeux et système", top: 0));
        var largest = scan.LargestFiles.Max(f => f.Bytes);
        foreach (var f in scan.LargestFiles)
        {
            var file = f;
            var row = Row(Icon(""), Path.GetFileName(f.Path), $"{Path.GetDirectoryName(f.Path)} · {f.Modified.ToString("d MMM yyyy", Fr)}", (double)f.Bytes / largest, StorageScanner.Format(f.Bytes), () => FileActions(file), out _, out _);
            row.Tag = f;
            Items.Children.Add(row);
        }
    }

    private void FileActions(StorageFile file)
    {
        var panel = new SidePanel(Path.GetFileName(file.Path), StorageScanner.Format(file.Bytes));
        panel.AddChoice("Afficher dans l'Explorateur", false, () =>
        {
            panel.Close();
            ShowInExplorer(file.Path);
        });
        // Les fichiers d'une application peuvent la casser : ils se gèrent depuis Windows.
        if (file.Category != StorageCategory.Apps)
            panel.AddChoice("Mettre à la corbeille", false, () =>
            {
                panel.Close();
                Trash(file);
            }, detail: "Restaurable");
        panel.AddChoice("Annuler", false, panel.Close);
        OverlayRequested?.Invoke(panel);
    }

    private void Trash(StorageFile file)
    {
        try
        {
            // Avec les dialogues de Windows : un fichier trop gros pour la corbeille n'est jamais effacé sans avertissement.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Core.Log.Error($"Mise à la corbeille impossible : {file.Path}", ex);
            ToastRequested?.Invoke("", "Impossible de déplacer ce fichier");
            return;
        }
        if (File.Exists(file.Path) || Scan is not { } scan) return;

        scan.LargestFiles.Remove(file);
        scan.Categories[file.Category] = Math.Max(0, scan.Categories.GetValueOrDefault(file.Category) - file.Bytes);
        StorageStore.Save();
        ToastRequested?.Invoke("", "Fichier mis à la corbeille : vide-la pour récupérer la place");
        Render(keepFocus: true);
        FocusFirstRow();
    }

    private static void ShowInExplorer(string path) => GameLauncher.Open("explorer.exe", $"/select,\"{path}\"");

    // =====================================================================
    // Briques visuelles
    // =====================================================================

    /// <summary>Ligne sélectionnable : visuel, titre et détail, barre éventuelle, taille à droite.</summary>
    private Button Row(UIElement leading, string title, string? detail, double? ratio, string? value, Action? click, out TextBlock? valueText, out Action<double>? meter, Brush? bar = null, double height = 58)
    {
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(height > 58 ? 56 : 42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (leading is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(leading);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.SemiBold, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail is not null)
            text.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = Res<Brush>("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        meter = null;
        if (ratio is { } r)
        {
            var track = Meter(r, bar ?? Res<Brush>("Accent"), out meter);
            track.Width = 110;
            track.Margin = new Thickness(18, 0, 0, 0);
            track.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(track, 2);
            grid.Children.Add(track);
        }

        valueText = null;
        if (value is not null)
        {
            valueText = new TextBlock { Text = value, FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = Res<Brush>("TextPrimary"), TextAlignment = TextAlignment.Right, Width = 82, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(valueText, 3);
            grid.Children.Add(valueText);
        }

        var button = new Button { Content = grid, Style = Res<Style>("RowButton"), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 1, 0, 1) };
        RowOptions.SetNoBar(button, true);
        RowOptions.SetFloating(button, true);
        button.GotKeyboardFocus += (_, _) =>
        {
            _menuHighlight.Dim(0.5);
            _highlight.MoveTo(button);
            _lastRow = button;
            RaiseHintsChanged();
        };
        button.Click += (_, _) => click?.Invoke();
        return button;
    }

    /// <summary>Jauge horizontale animée ; <paramref name="set"/> la déplace ensuite.</summary>
    private static Grid Meter(double ratio, Brush brush, out Action<double> set)
    {
        var track = new Grid { Height = 6 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) });
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = brush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Children.Add(fill);
        var current = ratio;
        void Apply() => fill.BeginAnimation(WidthProperty, new DoubleAnimation(current > 0 ? Math.Max(4, track.ActualWidth * current) : 0, Anim.Ms(500)) { EasingFunction = Anim.Out });
        track.SizeChanged += (_, _) => Apply();
        set = value =>
        {
            current = value;
            Apply();
        };
        return track;
    }

    private TextBlock Icon(string glyph)
        => new() { Text = glyph, FontFamily = Res<FontFamily>("Icons"), FontSize = 19, Foreground = Res<Brush>("TextPrimary") };

    private static Border Swatch(Color color)
        => new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left };

    private static FrameworkElement Cover(Game game)
    {
        var cover = new Grid { Width = 38, Height = 54, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2A)) };
        Rounded.SetRadius(cover, 6);
        var image = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(image, 90);
        ImageLoader.SetSource(image, game.CoverPath);
        cover.Children.Add(image);
        return cover;
    }

    private TextBlock Heading(string text, double top = 20)
        => new() { Text = text.ToUpperInvariant(), Style = Res<Style>("Label"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, top, 0, 8) };

    private TextBlock Note(string text)
        => new() { Text = text, FontSize = 13, Foreground = Res<Brush>("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 0) };

    private TextBlock Empty(string text)
        => new() { Text = text, FontSize = 14, Foreground = Res<Brush>("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 0) };

    private T Res<T>(string key) => (T)FindResource(key);

    private static string Ago(DateTime d)
    {
        var days = (DateTime.Today - d.Date).Days;
        return days switch
        {
            <= 0 => "aujourd'hui",
            1 => "hier",
            < 30 => $"il y a {days} jours",
            < 365 => $"il y a {days / 30} mois",
            < 730 => "il y a plus d'un an",
            _ => $"il y a {days / 365} ans",
        };
    }

    /// <summary>Barre du disque : un segment par catégorie, séparés de 2 px ; la piste vide est l'espace libre.</summary>
    private sealed class StackedBar
    {
        private readonly long _total;
        private readonly StackPanel _segments = new() { Orientation = Orientation.Horizontal };
        private readonly Dictionary<StorageCategory, Border> _bars = [];
        private Dictionary<StorageCategory, long> _values = [];
        public Grid Element { get; } = new() { Height = 14, Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) };

        public StackedBar(long total)
        {
            _total = Math.Max(1, total);
            Rounded.SetRadius(Element, 7);
            Element.Children.Add(_segments);
            foreach (var (category, name, _, color) in Categories)
            {
                var bar = new Border { Background = new SolidColorBrush(color), Width = 0, Margin = new Thickness(0, 0, 2, 0), ToolTip = name };
                _bars[category] = bar;
                _segments.Children.Add(bar);
            }
            Element.SizeChanged += (_, _) => Layout();
        }

        public void Update(Dictionary<StorageCategory, long> values)
        {
            _values = values;
            Layout();
        }

        private void Layout()
        {
            var width = Element.ActualWidth;
            if (width <= 0) return;
            // Les liens durs de Windows peuvent faire dépasser le total mesuré : la barre reste dans la piste.
            var scale = (double)Math.Max(_total, _values.Values.Sum());
            foreach (var (category, bar) in _bars)
            {
                var bytes = _values.GetValueOrDefault(category);
                bar.Margin = new Thickness(0, 0, bytes > 0 ? 2 : 0, 0);
                bar.ToolTip = $"{Categories.First(c => c.Category == category).Name} : {StorageScanner.Format(bytes)}";
                bar.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(0, width * bytes / scale - 2), Anim.Ms(500)) { EasingFunction = Anim.Out });
            }
        }
    }
}
