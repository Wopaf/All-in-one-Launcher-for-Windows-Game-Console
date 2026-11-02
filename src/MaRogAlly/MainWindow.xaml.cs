using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Metadata;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;
using MaRogAlly.Views;

namespace MaRogAlly;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly MetadataService _metadata;
    private readonly DesktopHost _desktop;
    private readonly EpicFreeGamesService _epic;
    private readonly DiscoverService _discover;
    private readonly AchievementService _achievements;
    /// <summary>Fiches des jeux du magasin (Découverte) : magasin Steam, logos en cache disque.</summary>
    private readonly SteamStore _steamStore;
    private readonly ImageCache _imageCache;
    private readonly Dictionary<int, GameMetadata> _storeMeta = [];
    private List<FreeGame> _freeGames = [];
    private DateTime _lastEpicFetch = DateTime.MinValue;
    private readonly Gamepad _pad = new();

    private readonly GamesView _installed = new() { InstalledOnly = true };
    private readonly GamesView _all = new();
    private readonly DiscoverView _discoverView = new();
    private readonly ExploreView _explore = new();
    private readonly GameSearch _search;
    private readonly TagService _tags;
    private readonly AppUpdater _appUpdater;
    private CancellationTokenSource? _searchCts;
    private readonly PlatformsView _platforms = new();
    private readonly AchievementsView _achievementsView = new();
    private readonly SettingsView _settingsView;
    private readonly (string Title, FrameworkElement View, string Key)[] _pages;
    private readonly List<TextBlock> _tabLabels = [];
    /// <summary>Pastille sur l'onglet Réglages : nombre de mises à jour Windows en attente.</summary>
    private int _page;
    /// <summary>Page sans onglet ouverte par-dessus l'onglet courant (tendances depuis Découverte, Réglages et Plateformes depuis le menu) : B y revient.</summary>
    private FrameworkElement? _subView;
    private FrameworkElement CurrentView => _subView ?? _pages[_page].View;

    private readonly List<OverlayBase> _overlays = [];
    private List<Game> _games = [];

    private bool _scanning;
    private DateTime _lastScan = DateTime.MinValue;
    private CancellationTokenSource? _enrichCts;

    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _heroPath;

    public MainWindow()
    {
        InitializeComponent();

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MaRogAlly/1.0");

        _library = new LibraryService(_settings, _http);
        _metadata = new MetadataService(_http, _settings);
        _desktop = new DesktopHost(this);
        // « --size 1280x672 » : simule un autre écran (ex. ROG Ally en 1920×1080 à 150 %, barre des tâches déduite).
        var sizeArg = Array.IndexOf(Environment.GetCommandLineArgs(), "--size");
        if (sizeArg >= 0 && Environment.GetCommandLineArgs().ElementAtOrDefault(sizeArg + 1)?.Split('x') is [var sw, var sh]
            && int.TryParse(sw, out var fw) && int.TryParse(sh, out var fh))
            _desktop.ForcedSize = (fw, fh);
        _epic = new EpicFreeGamesService(_http);
        _discover = new DiscoverService(_http);
        _achievements = new AchievementService(_http, _settings);
        _steamStore = new SteamStore(_http);
        _search = new GameSearch(_http, _discover);
        _tags = new TagService(_http);
        _appUpdater = new AppUpdater(_http);
        GameCategories.TagLabel = _tags.Label;
        _imageCache = new ImageCache(_http);
        _settingsView = new SettingsView(_settings);

        _pages =
        [
            ("Mes Jeux", _installed, "installed"),
            ("Bibliothèque", _all, "library"),
            ("Découverte", _explore, "explore"),
            ("Succès", _achievementsView, "achievements"),
        ];
        BuildTabs();
        WireViews();

        _pad.IsActive = () => _desktop.IsForeground;
        _pad.Action += OnNav;
        _pad.ReturnRequested += _desktop.BringToFront;
        _pad.ConnectionChanged += connected => Toast(connected ? "" : "", connected ? "Manette connectée" : "Manette déconnectée");

        _heroTimer.Tick += (_, _) => { _heroTimer.Stop(); ShowHero(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Anim.Exit(ToastBox, 0, -16, 260); };

        HintsBox.SizeChanged += (_, _) => PlaceHints();

        // Petit écran (ROG Ally) : barre du haut et aides plus serrées.
        SizeChanged += (_, _) =>
        {
            var compact = ActualHeight < 800;
            // Marge sous les onglets : le contenu des pages ne vient pas s'y coller.
            TopBar.Margin = compact ? new Thickness(64, 30, 64, 22) : new Thickness(64, 42, 64, 32);
            HintsBox.Margin = compact ? new Thickness(64, 4, 64, 10) : new Thickness(64, 6, 64, 20);
            ApplyUiScale();
            _settingsView.UpdateUiScale(UiScaleLabel(_settings.UiScale));
        };

        Loaded += OnLoaded;
        Activated += OnActivated;
        PreviewKeyDown += OnKeyDown;
        PreviewTextInput += OnTextInput;
        Closed += (_, _) => { _pad.Dispose(); _enrichCts?.Cancel(); };
    }

    // =====================================================================
    // Démarrage
    // =====================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UiSounds.Enabled = _settings.UiSounds && !Environment.GetCommandLineArgs().Contains("--render");
        UiSounds.PlayOpen();
        if (UpdateSnapshot.Load() is { } updates)
        {
            _windowsUpdates = updates.Windows;
            _asusApp = updates.Asus;
        }
        _ = UiSounds.InitAsync();
        PlayIntro();
        UpdateClock();
        // Barre de gauche : suit les fenêtres ouvertes tant que l'application est visible au premier plan.
        var dock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        dock.Tick += (_, _) => { if (_desktop.IsForeground) _ = RefreshDockAsync(); };
        dock.Start();
        _ = RefreshDockAsync();

        var pulse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        pulse.Tick += (_, _) =>
        {
            UpdateClock();
            _explore.RefreshCountdowns();
            RefreshDue();
        };
        pulse.Start();
        _pad.Start();

        // 1. Affichage immédiat du dernier scan connu.
        var cached = _library.LoadCached();
        foreach (var g in cached) _metadata.ApplyCached(g);
        _tags.Apply(cached);
        SetGames(cached);
        // « --tab N » ouvre directement un onglet (0 à 4) : pratique pour vérifier une page.
        var args = Environment.GetCommandLineArgs();
        var tabArg = Array.IndexOf(args, "--tab");
        var startPage = tabArg >= 0 && tabArg + 1 < args.Length && int.TryParse(args[tabArg + 1], out var t) ? t : 0;
        ShowPage(startPage, animate: false);
        if (args.Contains("--stats")) OpenStats();
        if (args.Contains("--detail") && _installed.Source.FirstOrDefault() is { } firstGame) OpenDetail(firstGame);
        if (args.Contains("--menu")) OpenSystemMenu();
        if (args.Contains("--store"))
        {
            // Fiche magasin d'un jeu de la Découverte non possédé, une fois les tendances chargées.
            var store = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            store.Tick += (_, _) =>
            {
                store.Stop();
                if (_discoverView.AllGames.FirstOrDefault(g => g.Owned is null) is { } d) OpenStoreDetail(d);
            };
            store.Start();
        }
        if (args.Contains("--web")) OpenOverlay(new WebPreview("Instant Gaming", "Hades", "https://www.instant-gaming.com/fr/rechercher/?q=Hades", ".search.listing-items"));
        if (args.Contains("--prompt")) OpenStorePrompt("Counter-Strike 2", PlatformInfo.Of(Platform.Steam), () => true, ("Rechercher sur Instant Gaming", () => { }));
        if (args.Contains("--sweep") && _overlays.LastOrDefault() is DetailView sweepView)
        {
            var replay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            replay.Tick += (_, _) => { replay.Stop(); sweepView.PreviewSweep(); };
            replay.Start();
        }
        if (args.Contains("--shot") && _overlays.LastOrDefault() is DetailView shotView)
            _ = Dispatcher.BeginInvoke(shotView.PreviewShots, DispatcherPriority.ApplicationIdle);
        _ = LoadFreeGamesAsync();
        _ = LoadDiscoverAsync();
        // Lanceurs prêts en arrière-plan (pas lors des vérifications d'affichage « --tab »).
        _ = LoadLaunchersAsync(start: tabArg < 0);
        // « --category rogue » et « --categories » : bouton actif et panneau de choix, pour vérifier l'affichage.
        var categoryArg = Array.IndexOf(args, "--category");
        if (categoryArg >= 0 && categoryArg + 1 < args.Length)
        {
            _all.Filter.Category = args[categoryArg + 1];
            _all.Refresh();
        }
        if (args.Contains("--categories")) OpenCategoryPicker(_all);
        var searchArg = Array.IndexOf(args, "--search");
        if (searchArg >= 0 && searchArg + 1 < args.Length) _ = SearchStoresAsync(args[searchArg + 1]);
        // « --render fichier.png » : enregistre l'écran dans une image puis se ferme, sans prendre le focus.
        var renderArg = Array.IndexOf(args, "--render");
        if (renderArg >= 0 && renderArg + 1 < args.Length)
        {
            var render = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            render.Tick += (_, _) =>
            {
                render.Stop();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(this);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var file = File.Create(args[renderArg + 1])) png.Save(file);
                Application.Current.Shutdown();
            };
            render.Start();
        }
        if (args.Contains("--platforms")) OpenSubPage(_platforms);
        // « --dock options,windows » : boutons de la barre de gauche pressés à la suite, pour vérifier l'affichage.
        var dockArg = Array.IndexOf(args, "--dock");
        if (dockArg >= 0 && args.ElementAtOrDefault(dockArg + 1) is { } dockButtons)
        {
            await Task.Delay(1500); // le temps de relever les applications ouvertes
            foreach (var button in dockButtons.Split(','))
            {
                await Task.Delay(500);
                if (button == "enter") Dock.Enter();
                else if (button is "left" or "right") OnNav(button == "left" ? NavAction.Left : NavAction.Right);
                else if (button == "windows") ToggleDockPanel(WindowsMenu, OpenWindowsMenu);
                else if (button == "background") ToggleDockPanel(BackgroundApps, OpenBackgroundApps);
                else ToggleDockPanel(DockOptions, OpenDockOptions);
            }
        }
        if (args.Contains("--settings"))
        {
            _settingsView.PreviewCategory("Au démarrage");
            OpenSubPage(_settingsView);
        }
        _ = CheckUpdatesAsync();
        _ = AppUpdater.CleanUpAsync();
        if (args.Contains(AppUpdater.UpdatedArg)) Toast("", $"Ma ROG Ally est à jour (version {AppUpdater.CurrentText})");
        if (_settings.CheckAppUpdates) _ = CheckAppUpdateAsync();
        // « --apply-update » : installe sans rien demander, pour vérifier le remplacement de l'exe sur une copie de test.
        if (args.Contains("--apply-update")) _ = ApplyUpdateForTestAsync();
        _ready = true;

        // 2. Scan complet de toutes les plateformes, puis enrichissement en ligne.
        await ScanAsync(initial: true);
    }

    /// <summary>Derrière toutes les fenêtres (« --render »).</summary>
    public void SendToBackNow() => _desktop.SendToBack();

    /// <summary>Lanceurs installés : proposés dans les réglages, et ouverts avec l'application sauf ceux désactivés.</summary>
    private async Task LoadLaunchersAsync(bool start)
    {
        var installed = await Task.Run(LibraryService.InstalledLaunchers);
        _settingsView.SetLaunchers(installed);
        if (start)
            LauncherWarmup.Start(installed.Where(p => !_settings.DisabledLaunchers.Contains(p.Id)), () => Dispatcher.BeginInvoke(() => Activate()));
    }

    private void PlayIntro()
    {
        var scale = (ScaleTransform)IntroContent.RenderTransform;
        // Environ 3,5 s : le logo apparaît, grossit doucement, reste affiché, puis s'efface.
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, Anim.Ms(2900)) { EasingFunction = Anim.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, Anim.Ms(2900)) { EasingFunction = Anim.Out });
        IntroContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(900)));
        // Une vague, du centre vers les bords, qui laisse voir le motif hexagonal.
        PlayIntroWave(IntroWave, TimeSpan.FromMilliseconds(1100));
        var fade = new DoubleAnimation(1, 0, Anim.Ms(500)) { BeginTime = TimeSpan.FromMilliseconds(3000), EasingFunction = Anim.InOut };
        fade.Completed += (_, _) => Intro.Visibility = Visibility.Collapsed;
        Intro.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Vague circulaire : un anneau opaque (front net, traîne en fondu) dans un masque radial, centré sur l'écran.
    /// Elle part d'un petit cercle autour du logo, se resserre un instant (anticipation),
    /// puis s'élargit vite et en douceur jusqu'aux coins ; le motif n'apparaît qu'à son passage.
    /// </summary>
    private static void PlayIntroWave(FrameworkElement target, TimeSpan begin)
    {
        // Taille de la zone elle-même (l'interface peut être mise à l'échelle) : le centre tombe sur le logo.
        var w = Math.Max(1, target.ActualWidth);
        var h = Math.Max(1, target.ActualHeight);
        var radius = Math.Sqrt(w * w + h * h) / 2;
        var stops = new[]
        {
            new GradientStop(Colors.Transparent, -1),  // traîne : le motif s'efface derrière la vague
            new GradientStop(Colors.Black, -1),        // crête
            new GradientStop(Colors.Transparent, -1),  // front
        };
        target.OpacityMask = new RadialGradientBrush(new GradientStopCollection(stops))
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(w / 2, h / 2),
            GradientOrigin = new Point(w / 2, h / 2),
            RadiusX = radius,
            RadiusY = radius,
        };

        // Crête : 0,10 → 0,05 (recul) → 1,2 (au-delà des coins) : très rapide au départ, puis un fort ralenti à la fin.
        var expand = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        void Move(GradientStop stop, double shift)
        {
            var animation = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.10 + shift, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.05 + shift, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.2 + shift, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900)), expand));
            stop.BeginAnimation(GradientStop.OffsetProperty, animation);
        }
        Move(stops[0], -0.2);
        Move(stops[1], 0);
        Move(stops[2], 0.04);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _ = RefreshDockAsync();
        RefreshDue(returned: true);
    }

    // =====================================================================
    // Actualisation : tout est décidé ici
    // =====================================================================

    private bool _ready, _libraryDirty, _achievementsDirty, _updatesDirty, _updatesFailed;

    /// <summary>
    /// Actualise ce qui doit l'être, toutes les 5 secondes et à chaque retour sur l'application.
    /// Uniquement au premier plan : rien ne tourne pendant un jeu, et le retour rattrape tout ce qui est en retard.
    /// Les écrans ne déclenchent rien : ils affichent le dernier résultat, qui se met à jour sous les yeux.
    /// </summary>
    private void RefreshDue(bool returned = false)
    {
        if (!_ready || (!returned && !_desktop.IsForeground)) return;
        var now = DateTime.Now;

        if (CurrentView == _platforms) _platforms.RefreshRunning();

        // Jeux : disque à chaque passage (rapide) ; bibliothèques en ligne au retour et toutes les 30 min.
        if (!_scanning && _lastScan != DateTime.MinValue
            && (returned && (_libraryDirty || now - _lastScan > TimeSpan.FromMinutes(2)) || now - _lastScan > TimeSpan.FromMinutes(30)))
        {
            _libraryDirty = false;
            _ = ScanAsync(initial: false);
        }
        else _ = WatchInstallsAsync();

        // Mises à jour : toutes les 30 min (5 min après un échec), et au retour de Windows Update ou d'Armoury Crate.
        if (returned && _updatesDirty)
        {
            _updatesDirty = false;
            _lastUpdateCheck = default;
        }
        if (now - _lastUpdateCheck >= TimeSpan.FromMinutes(_updatesFailed ? 5 : 30)) _ = CheckUpdatesAsync();

        // Nouvelle version de l'application : toutes les 6 h (GitHub limite le nombre de requêtes).
        if (_settings.CheckAppUpdates && now - _lastAppUpdateCheck >= TimeSpan.FromHours(6)) _ = CheckAppUpdateAsync();

        // Offres Epic : toutes les 10 min de minuit à 1 h 10, et dès qu'elles sont périmées (nouveau jour, offre terminée).
        if (now - _lastEpicFetch > TimeSpan.FromMinutes(10)
            && (now.TimeOfDay <= new TimeSpan(1, 10, 0) || now.Date != _lastEpicFetch.Date || _freeGames.Count == 0 || _freeGames.Any(g => g.IsCurrent && g.End <= now)))
            _ = LoadFreeGamesAsync();

        // Tendances Steam : toutes les 3 h (5 min tant qu'on n'a rien à montrer).
        if (now - _lastDiscoverFetch > TimeSpan.FromHours(_discoverView.AllGames.Any() ? 3 : 1.0 / 12)) _ = LoadDiscoverAsync();

        // Succès : toutes les 30 min (et après une partie, avec le scan du retour).
        if (!_scanning && _games.Count > 0 && now - _lastAchievementsFetch > TimeSpan.FromMinutes(30)) _ = LoadAchievementsAsync();
    }

    /// <param name="localOnly">Disque seulement : reflète tout de suite une installation ou une désinstallation.</param>
    private async Task ScanAsync(bool initial, bool localOnly = false)
    {
        if (_scanning) return;
        _scanning = true;
        List<Game>? scanned = null;
        if (!localOnly) Activity("scan", "Actualisation des jeux", $"Analyse de {PlatformInfo.All.Count} plateformes (jeux installés et bibliothèques)…");
        try
        {
            var before = _games.Select(g => g.Id).ToHashSet();
            var installedBefore = _games.Where(g => g.IsInstalled).ToDictionary(g => g.Id, g => g.DisplayTitle);
            var (games, statuses) = await _library.ScanAsync(_games, CancellationToken.None, online: !localOnly);
            foreach (var g in games) _metadata.ApplyCached(g);
            _tags.Apply(games);
            SetGames(games);
            _platforms.SetData(statuses, games);
            if (!localOnly && (_achievementsDirty || DateTime.Now - _lastAchievementsFetch > TimeSpan.FromMinutes(30))) _ = LoadAchievementsAsync();
            _epicInstalled = statuses.Any(s => s.Info.Id == Platform.Epic && s.ClientInstalled);
            if (!localOnly) _lastScan = DateTime.Now;
            EndActivity("scan");

            var added = before.Count == 0 ? 0 : games.Count(g => !before.Contains(g.Id));
            var nowInstalled = games.Where(g => g.IsInstalled).ToList();
            var newlyInstalled = installedBefore.Count == 0 ? [] : nowInstalled.Where(g => !installedBefore.ContainsKey(g.Id)).ToList();
            var removed = installedBefore.Where(kv => nowInstalled.All(g => g.Id != kv.Key)).Select(kv => kv.Value).ToList();
            if (newlyInstalled.Count == 1) Toast("", $"{newlyInstalled[0].DisplayTitle} est installé");
            else if (removed.Count == 1 && newlyInstalled.Count == 0) Toast("", $"{removed[0]} a été désinstallé");
            else if (added > 0) Toast("", $"{added} nouveau{(added > 1 ? "x" : "")} jeu{(added > 1 ? "x" : "")} détecté{(added > 1 ? "s" : "")}");
            else if (initial && before.Count == 0) Toast("", $"{games.Count(g => g.IsInstalled)} jeux installés trouvés");

            scanned = games;
        }
        catch (Exception ex)
        {
            Log.Error("Scan impossible", ex);
            Toast("", "Le scan a échoué (voir log.txt)");
        }
        finally
        {
            _scanning = false;
            EndActivity("scan");
        }
        // Les fiches se téléchargent ensuite : la détection des installations reste active pendant ce temps.
        if (localOnly || scanned is null) return;
        await RefreshTagsAsync();
        await EnrichAsync(scanned);
        // Les fiches relient de nouveaux jeux à Steam : leurs tags suivent.
        await RefreshTagsAsync();
    }

    private async Task RefreshTagsAsync()
    {
        if (!await _tags.FetchMissingAsync(_games, CancellationToken.None)) return;
        _tags.Apply(_games);
        if (_all.Filter.Category is not null) _all.Refresh();
    }

    private bool _watchingInstalls;

    /// <summary>Dès que les jeux installés sur le disque changent : listes à jour tout de suite, puis scan complet (fiche du nouveau jeu).</summary>
    private async Task WatchInstallsAsync()
    {
        if (_scanning || _watchingInstalls || _lastScan == DateTime.MinValue) return;
        _watchingInstalls = true;
        try
        {
            var onDisk = await _library.InstalledIdsAsync(CancellationToken.None);
            if (_scanning) return;
            var shown = _games.Where(g => g.IsInstalled).Select(g => g.Id).ToHashSet();
            if (onDisk.SetEquals(shown)) return;
            Log.Info($"Installations : {onDisk.Except(shown).Count()} jeu(x) en plus, {shown.Except(onDisk).Count()} en moins");
            await ScanAsync(initial: false, localOnly: true);
            await ScanAsync(initial: false);
        }
        catch (Exception ex) { Log.Error("Suivi des installations", ex); }
        finally { _watchingInstalls = false; }
    }

    /// <summary>Télécharge fiches et visuels manquants : jeux installés d'abord, puis le reste.</summary>
    private async Task EnrichAsync(IReadOnlyList<Game> games, bool retryMissing = false)
    {
        _enrichCts?.Cancel();
        var cts = _enrichCts = new CancellationTokenSource();
        var ordered = games.OrderByDescending(g => g.IsInstalled).ToList();
        var started = DateTime.Now;
        var phase = "";
        string? current = null;
        var progress = new Progress<EnrichProgress>(p =>
        {
            if (_enrichCts != cts) return; // rapport en retard d'un enrichissement annulé
            if (p.Phase != phase)
            {
                // Nouvelle passe : l'estimation du temps restant repart de zéro.
                phase = p.Phase;
                started = DateTime.Now;
                current = null;
            }
            if (p.Total == 0 || p.Done >= p.Total)
            {
                EndActivity("meta");
                return;
            }
            current = p.Current ?? current;
            var title = p.Phase == "Visuels" ? "Téléchargement des visuels" : "Téléchargement des fiches de jeux";
            Activity("meta", title,
                $"{p.Done + 1} sur {p.Total}{(current is null ? "" : $" · {current}")} · {Remaining(started, p.Done, p.Total)}",
                (double)p.Done / p.Total);
        });
        try
        {
            await _metadata.EnrichAsync(ordered, progress, cts.Token, retryMissing);
            // Les tris par note ou date dépendent des fiches : on réapplique.
            _installed.Refresh();
            _all.Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Enrichissement des fiches", ex); }
        finally
        {
            if (_enrichCts == cts)
            {
                _enrichCts = null;
                EndActivity("meta");
            }
        }
    }

    private void SetGames(List<Game> games)
    {
        _games = games;
        _installed.SetGames(games);
        _all.SetGames(games);
        _installed.SetActive(CurrentGames == _installed);
        _all.SetActive(CurrentGames == _all);
        MarkOwnedFreeGames();
        _discoverView.MatchOwned(games);
    }

    // =====================================================================
    // Succès (toutes plateformes)
    // =====================================================================

    private DateTime _lastAchievementsFetch;
    private List<AchievementGame> _achievementList = [];
    private bool _loadingAchievements, _achievementsShown;

    private readonly SemaphoreSlim _achievementCoverGate = new(3);

    /// <summary>
    /// Jaquette d'un jeu à succès absent de la bibliothèque telle quelle (connu seulement de GOG Galaxy, ou plus dans la liste Steam) :
    /// le même titre sur une autre plateforme, sinon le magasin Steam.
    /// </summary>
    private async Task<string?> AchievementCoverAsync(AchievementGame a)
    {
        var games = _games;
        var known = await Task.Run(() =>
        {
            var titles = Text.SearchVariants(a.Title).Select(Text.Normalize).Where(t => t.Length > 0).ToHashSet();
            return games.FirstOrDefault(g => g.CoverPath is not null && (titles.Contains(Text.Normalize(g.Title)) || titles.Contains(Text.Normalize(g.DisplayTitle))));
        });
        if (known is not null) return known.CoverPath;

        var key = $"ach-{a.Key}";
        if (ImageCache.Existing(key, "cover") is { } path) return path;
        await _achievementCoverGate.WaitAsync();
        try
        {
            int? id = a.Key.StartsWith("steam_", StringComparison.Ordinal) && int.TryParse(a.Key[6..], out var appId)
                ? appId
                : await _steamStore.FindAppIdAsync(a.Title, CancellationToken.None);
            return id is int steam
                ? await _imageCache.GetAsync(key, "cover", SteamStore.CoverUrls(steam).Concat(SteamStore.HeaderUrls(steam)), CancellationToken.None)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Jaquette des succès ({a.Title})", ex);
            return null;
        }
        finally { _achievementCoverGate.Release(); }
    }

    private async Task LoadAchievementsAsync()
    {
        if (_loadingAchievements) return;
        _loadingAchievements = true;
        _achievementsDirty = false;
        _lastAchievementsFetch = DateTime.Now;
        var games = _games.ToList();
        Activity("achievements", "Succès", "Lecture des succès de toutes les plateformes…");
        try
        {
            // Succès déjà connus tout de suite (cache Steam, base Galaxy), puis mise à jour en ligne.
            if (!_achievementsShown)
            {
                _achievementList = await Task.Run(() => _achievements.Load(games));
                _achievementsView.SetData(_achievementList, loaded: false);
                RefreshDetailAchievements();
                _achievementsShown = true;
            }
            var list = await Task.Run(() => _achievements.RefreshAsync(games, (done, total) => Dispatcher.BeginInvoke(() =>
                Activity("achievements", "Succès", $"Récupération des succès Steam ({done} / {total} jeux)…", (double)done / total)),
                CancellationToken.None));
            _achievementList = list;
            _achievementsView.SetData(list, loaded: true, _achievements.SteamProfilePrivate);
            RefreshDetailAchievements();
        }
        catch (Exception ex)
        {
            Log.Error("Succès", ex);
        }
        finally
        {
            _loadingAchievements = false;
            EndActivity("achievements");
        }
    }

    // =====================================================================
    // Découverte (tendances Steam)
    // =====================================================================

    private DateTime _lastDiscoverFetch;

    private async Task LoadDiscoverAsync()
    {
        _lastDiscoverFetch = DateTime.Now;
        // Dernières tendances connues tout de suite, puis mise à jour en ligne.
        if (!_discoverView.AllGames.Any())
        {
            var cached = DiscoverService.LoadCached();
            DiscoverService.ApplyCachedImages(cached.SelectMany(c => c.Games));
            _discoverView.SetSections(cached, loaded: false);
            _explore.SetTrends(cached);
        }
        Activity("discover", "Découverte", "Récupération des tendances Steam…");
        try
        {
            var sections = await _discover.FetchAsync(CancellationToken.None);
            if (sections.Count == 0 && _discoverView.AllGames.Any()) return; // hors ligne : on garde l'affichage actuel
            DiscoverService.ApplyCachedImages(sections.SelectMany(c => c.Games));
            _discoverView.SetSections(sections, loaded: true);
            _explore.SetTrends(sections);
            var games = _discoverView.AllGames.ToList();
            Activity("discover", "Découverte", $"Téléchargement des jaquettes de {games.Select(g => g.AppId).Distinct().Count()} jeux…");
            await _discover.LoadImagesAsync(games, CancellationToken.None);
            if (_subView == _discoverView && _overlays.Count == 0) _discoverView.ImagesLoaded();
            else if (CurrentView == _explore && _overlays.Count == 0) SetHero(_explore.CurrentHero);
        }
        catch (Exception ex) { Log.Error("Découverte", ex); }
        finally { EndActivity("discover"); }
    }

    // =====================================================================
    // Mises à jour (Windows Update, Armoury Crate)
    // =====================================================================

    private DateTime _lastUpdateCheck;
    private AsusAppInfo? _asusApp;
    private WindowsUpdateStatus? _windowsUpdates;
    private bool _checkingUpdates;

    private async Task CheckUpdatesAsync()
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        _lastUpdateCheck = DateTime.Now;
        try
        {
            _asusApp = await Task.Run(UpdateChecker.FindArmouryCrate);
            var status = await UpdateChecker.CheckWindowsAsync();
            // Échec (hors ligne…) : on garde le dernier résultat connu et on réessaie plus tôt.
            _updatesFailed = status.Error is not null;
            if (!_updatesFailed || _windowsUpdates is null) _windowsUpdates = status;
            new UpdateSnapshot(_windowsUpdates, _asusApp).Save();
            // Menu ouvert pendant la vérification : ses tuiles suivent.
            if (_systemMenu is { } menu)
            {
                var (winText, winColor) = MainMenu.WindowsStatus(_windowsUpdates);
                menu.UpdateTile("Windows", winText, winColor);
                var (asusText, asusColor) = MainMenu.AsusStatus(_asusApp, true);
                menu.UpdateTile("Armoury Crate", asusText, asusColor);
            }
        }
        finally { _checkingUpdates = false; }
    }

    // =====================================================================
    // Mise à jour de l'application (versions publiées sur GitHub)
    // =====================================================================

    private AppRelease? _appRelease;
    private DateTime _lastAppUpdateCheck;
    private bool _installingAppUpdate;

    private (string Text, Color Color) AppUpdateStatus => _appRelease is { } r
        ? ($"Version {r.Version.ToString(3)} disponible", Color.FromRgb(0xFF, 0xB8, 0x3D))
        : ($"Version {AppUpdater.CurrentText}", Color.FromRgb(0x6B, 0xD3, 0x6B));

    private async Task CheckAppUpdateAsync()
    {
        _lastAppUpdateCheck = DateTime.Now;
        var known = _appRelease?.Version;
        _appRelease = await _appUpdater.CheckAsync(CancellationToken.None) ?? _appRelease;
        if (_appRelease is not { } release || release.Version == known) return;
        Toast("", $"Ma ROG Ally {release.Version.ToString(3)} est disponible : voir le menu");
        var (text, color) = AppUpdateStatus;
        _systemMenu?.UpdateTile(AppTile, text, color);
    }

    private const string AppTile = "Ma ROG Ally";

    /// <summary>Tuile du menu ou « Vérifier maintenant » : vérifie, puis propose l'installation si une version existe.</summary>
    private async Task ManualAppUpdateAsync()
    {
        if (_appRelease is null)
        {
            Toast("", "Recherche d'une nouvelle version…");
            await CheckAppUpdateAsync();
            if (_appRelease is null)
            {
                Toast("", $"Ma ROG Ally est à jour (version {AppUpdater.CurrentText})");
                return;
            }
        }
        OpenAppUpdatePrompt(_appRelease);
    }

    private void OpenAppUpdatePrompt(AppRelease release)
    {
        var panel = new SidePanel("Mise à jour", $"Version {AppUpdater.CurrentText} → {release.Version.ToString(3)}");
        panel.SetHints("A:Valider|B:Fermer");
        panel.AddChoice("Installer et redémarrer", false, () =>
        {
            panel.Close();
            _ = InstallAppUpdateAsync(release);
        }, detail: $"{release.Size / (1024 * 1024)} Mo");
        panel.AddChoice("Plus tard", false, panel.Close);
        OpenOverlay(panel);
    }

    private async Task InstallAppUpdateAsync(AppRelease release)
    {
        if (_installingAppUpdate) return;
        if (!AppUpdater.CanApply)
        {
            Toast("", "Mise à jour possible seulement depuis MaRogAlly.exe");
            return;
        }
        _installingAppUpdate = true;
        var title = $"Mise à jour vers {release.Version.ToString(3)}";
        try
        {
            Activity("appupdate", title, "Téléchargement…", 0);
            var file = await _appUpdater.DownloadAsync(release, p => Dispatcher.BeginInvoke(() => Activity("appupdate", title, "Téléchargement…", p)), CancellationToken.None);
            AppUpdater.ApplyAndRestart(file);
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour de l'application", ex);
            Toast("", "La mise à jour a échoué (voir log.txt)");
        }
        finally
        {
            _installingAppUpdate = false;
            EndActivity("appupdate");
        }
    }

    private async Task ApplyUpdateForTestAsync()
    {
        await CheckAppUpdateAsync();
        if (_appRelease is { } release) await InstallAppUpdateAsync(release);
    }

    private void OpenStorePage(DiscoverGame g)
    {
        OpenStorePrompt(g.Name, PlatformInfo.Of(Platform.Steam), () => OpenSteamStore(g),
            ("Rechercher sur Instant Gaming", () => OpenInstantGaming(g)));
    }

    /// <summary>Page Steam du jeu : dans le client Steam s'il est installé, sinon dans le navigateur.</summary>
    private static bool OpenSteamStore(DiscoverGame g)
    {
        using var steam = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("steam");
        return GameLauncher.Open(steam is not null ? $"steam://store/{g.AppId}" : g.StoreUrl);
    }

    private void OpenInstantGaming(DiscoverGame g)
        => OpenOverlay(new WebPreview("Instant Gaming", g.Name, g.InstantGamingUrl, ".search.listing-items"));

    /// <summary>
    /// Fiche d'un jeu non possédé (Découverte) : affichée tout de suite avec ce que l'on sait,
    /// puis complétée par le magasin Steam (description, captures, studio, logo).
    /// </summary>
    private void OpenStoreDetail(DiscoverGame d)
    {
        var known = _storeMeta.GetValueOrDefault(d.AppId);
        var game = new Game
        {
            Platform = Platform.Steam,
            PlatformId = d.AppId.ToString(),
            Title = d.Name,
            SteamAppId = d.AppId,
            CoverPath = d.CoverPath,
            HeroPath = d.HeroPath,
            LogoPath = ImageCache.Existing($"steam-{d.AppId}", "logo"),
            Meta = known ?? new GameMetadata { Title = d.Name, Description = d.Description, Genres = d.Genres, ReleaseDate = d.Release },
        };
        var detail = new DetailView();
        detail.SetGame(game);
        detail.SetStore(d);
        detail.SteamRequested += g =>
        {
            Toast("", OpenSteamStore(g) ? "Ouverture de la page Steam…" : "Impossible d'ouvrir Steam");
        };
        detail.InstantGamingRequested += OpenInstantGaming;
        OpenOverlay(detail);
        SetHero(d.HeroPath ?? d.CoverPath);
        if (known is null)
        {
            detail.ShowLoadingShots();
            _ = CompleteStoreDetailAsync(d, game, detail);
        }
    }

    private async Task CompleteStoreDetailAsync(DiscoverGame d, Game game, DetailView detail)
    {
        try
        {
            var meta = new GameMetadata();
            if (!await _steamStore.FillAsync(d.AppId, meta, CancellationToken.None))
            {
                detail.ClearLoadingShots();
                return;
            }
            _storeMeta[d.AppId] = meta;
            var logo = await _imageCache.GetAsync($"steam-{d.AppId}", "logo", meta.Logos, CancellationToken.None);
            if (!_overlays.Contains(detail)) return;
            game.Meta = meta;
            game.LogoPath = logo;
            detail.SetGame(game);
            detail.SetStore(d);
        }
        catch (Exception ex)
        {
            Log.Error($"Fiche magasin {d.AppId}", ex);
            detail.ClearLoadingShots();
        }
    }

    /// <summary>Écran sombre de confirmation avant d'ouvrir la page d'un jeu dans une plateforme.</summary>
    private void OpenStorePrompt(string title, PlatformInfo platform, Func<bool> open,
        (string Label, Action Run)? alternative = null)
    {
        var prompt = new StorePrompt(title, platform, open);
        if (alternative is { } alt) prompt.AddAlternative(alt.Label, alt.Run);
        OpenOverlay(prompt);
    }

    // =====================================================================
    // Jeux offerts par Epic (onglet Découverte)
    // =====================================================================

    /// <summary>Lanceur Epic présent : les offres s'ouvrent dedans plutôt que dans le navigateur.</summary>
    private bool _epicInstalled;

    private async Task LoadFreeGamesAsync()
    {
        _lastEpicFetch = DateTime.Now;
        var first = _freeGames.Count == 0;
        Activity("epic", "Offres Epic Games", "Récupération des jeux offerts cette semaine…");
        try
        {
            await LoadFreeGamesCoreAsync(first);
        }
        finally
        {
            EndActivity("epic");
        }
    }

    private async Task LoadFreeGamesCoreAsync(bool first)
    {
        var games = await _epic.FetchAsync(CancellationToken.None);
        if (games.Count == 0 && _freeGames.Count > 0) return; // hors ligne : on garde l'affichage actuel
        _freeGames = games;
        MarkOwnedFreeGames();
        _explore.SetFreeGames(games);

        var current = games.Count(g => g.IsCurrent);
        if (first && current > 0) Toast("", $"{current} jeu{(current > 1 ? "x" : "")} offert{(current > 1 ? "s" : "")} sur Epic cette semaine");

        Activity("epic", "Offres Epic Games", $"Téléchargement des visuels de {games.Count} jeu{(games.Count > 1 ? "x" : "")}…");
        await _epic.LoadImagesAsync(games, CancellationToken.None);
        if (CurrentView == _explore && _overlays.Count == 0) SetHero(_explore.CurrentHero);
    }

    /// <summary>Signale les jeux offerts déjà présents dans la bibliothèque Epic.</summary>
    private void MarkOwnedFreeGames()
    {
        var owned = _games.Where(g => g.Platform == Platform.Epic).Select(g => Text.Normalize(g.Title)).ToHashSet();
        foreach (var f in _freeGames) f.IsOwned = owned.Contains(Text.Normalize(f.Title));
    }

    // =====================================================================
    // Onglets
    // =====================================================================

    private void BuildTabs()
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = _pages[i].Title,
                FontFamily = (FontFamily)FindResource("Display"),
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                // Marge intérieure : la taille du badge de l'onglet actif.
                Padding = new Thickness(18, 6, 18, 7),
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x74, 0x84)),
                Cursor = Cursors.Hand,
            };
            label.MouseLeftButtonUp += (_, _) => ShowPage(index);
            _tabLabels.Add(label);
            // Le libellé est dans une grille pour pouvoir y poser une pastille de notification.
            var tab = new Grid { Margin = new Thickness(0, 0, 6, 0) };
            tab.Children.Add(label);
            Tabs.Children.Add(tab);
            _pages[i].View.Visibility = Visibility.Collapsed;
            Pages.Children.Add(_pages[i].View);
        }
        foreach (var sub in new FrameworkElement[] { _discoverView, _settingsView, _platforms })
        {
            sub.Visibility = Visibility.Collapsed;
            Pages.Children.Add(sub);
        }

        foreach (var (_, view, key) in _pages)
            if (view is GamesView gv && _settings.Sorts.TryGetValue(key, out var sort))
                gv.Filter.Sort = sort;
    }

    private void ShowPage(int index, bool animate = true)
    {
        index = (index + _pages.Length) % _pages.Length;
        var direction = Math.Sign(index - _page);
        var old = CurrentView;
        _subView = null;
        SyncTopBar();
        var next = _pages[index].View;

        if (old != next && animate)
            Anim.Exit(old, -24 * direction, 0, 280, () => { if (CurrentView != old) old.Visibility = Visibility.Collapsed; });
        else if (old != next)
            old.Visibility = Visibility.Collapsed;

        _page = index;
        _installed.SetActive(next == _installed);
        _all.SetActive(next == _all);
        // Fondu enchaîné : l'ancienne page s'efface pendant que la nouvelle glisse doucement à sa place.
        var stillVisible = next.Visibility == Visibility.Visible && old != next;
        next.Visibility = Visibility.Visible;
        if (animate && stillVisible) Anim.Resume(next, 420);
        else if (animate) Anim.Enter(next, 32 * (direction == 0 ? 1 : direction), 0, 460, delay: 50);
        else next.Opacity = 1;

        // Onglet actif et soulignement animé.
        for (var i = 0; i < _tabLabels.Count; i++)
        {
            var target = i == index ? Color.FromRgb(0xF3, 0xF5, 0xF9) : Color.FromRgb(0x6B, 0x74, 0x84);
            ((SolidColorBrush)_tabLabels[i].Foreground).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, Anim.Ms(250)));
        }
        Dispatcher.BeginInvoke(MoveTabIndicator, DispatcherPriority.Loaded);
        OnViewShown(next);
    }

    /// <summary>Focus et fond de la page qui vient de s'afficher.</summary>
    private void OnViewShown(FrameworkElement view)
    {
        switch (view)
        {
            case GamesView gv:
                OnSelectionChanged(gv.Selected);
                break;
            case DiscoverView dv:
                dv.FocusFirst();
                SetHero(dv.CurrentHero);
                break;
            case ExploreView ev:
                ev.FocusFirst();
                SetHero(ev.CurrentHero);
                break;
            case PlatformsView pv:
                pv.FocusFirst();
                pv.RefreshRunning();
                SetHero(null);
                break;
            case AchievementsView av:
                // Pas de visuel de jeu : seulement le fond de l'application.
                av.FocusFirst();
                SetHero(null);
                if (DateTime.Now - _lastAchievementsFetch > TimeSpan.FromMinutes(10)) _ = LoadAchievementsAsync();
                break;
            case SettingsView sv:
                sv.FocusFirst();
                SetHero(null);
                break;
        }
        UpdateHints();
    }

    private void MoveTabIndicator()
    {
        var label = _tabLabels[_page];
        if (label.ActualWidth == 0) return;
        var x = label.TranslatePoint(new Point(0, 0), Tabs).X;
        var t = (TranslateTransform)TabIndicator.RenderTransform;
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Anim.Ms(380)) { EasingFunction = Anim.Spring });
        TabIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(label.ActualWidth, Anim.Ms(380)) { EasingFunction = Anim.Out });
        TabIndicator.Height = label.ActualHeight;
        TabIndicator.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Ouvre la page des tendances Steam (depuis les tuiles de l'onglet Découverte) sur la liste demandée.</summary>
    private void OpenDiscover(string key)
    {
        _discoverView.ShowSection(key);
        OpenSubPage(_discoverView);
    }

    /// <summary>La barre d'onglets s'efface dans les Réglages et les Plateformes (et sous les pages plein écran).</summary>
    private void SyncTopBar()
    {
        var visible = _subView is not (SettingsView or PlatformsView) && !_overlays.Any(o => o is DetailView or StatsView or KeyboardPanel);
        Anim.Fade(TopBar, visible ? 1 : 0, visible ? 300 : 250);
        TopBar.IsHitTestVisible = visible;
    }

    /// <summary>Affiche une page sans onglet par-dessus l'onglet courant.</summary>
    private void OpenSubPage(FrameworkElement view)
    {
        if (_subView == view) return;
        var from = CurrentView;
        _subView = view;
        SyncTopBar();
        Anim.Exit(from, 0, -40, 180, () => { if (CurrentView != from) from.Visibility = Visibility.Collapsed; });
        view.Visibility = Visibility.Visible;
        Anim.Enter(view, 0, 50, 340);
        OnViewShown(view);
        UpdateHints();
    }

    /// <summary>B sur une page sans onglet : retour à l'onglet d'où on l'a ouverte.</summary>
    private void CloseSubPage()
    {
        if (_subView is not { } view) return;
        _subView = null;
        SyncTopBar();
        Anim.Exit(view, 0, 40, 180, () => { if (CurrentView != view) view.Visibility = Visibility.Collapsed; });
        var page = _pages[_page].View;
        page.Visibility = Visibility.Visible;
        Anim.Enter(page, 0, -50, 340);
        OnViewShown(page);
        UpdateHints();
    }

    private void PrevTab_Click(object sender, MouseButtonEventArgs e) => ShowPage(_page - 1);
    private void NextTab_Click(object sender, MouseButtonEventArgs e) => ShowPage(_page + 1);

    private GamesView? CurrentGames => _pages[_page].View as GamesView;

    // =====================================================================
    // Câblage des vues
    // =====================================================================

    private void WireViews()
    {
        foreach (var gv in new[] { _installed, _all })
        {
            gv.SelectionChanged += g => { if (gv == CurrentGames && _overlays.Count == 0) OnSelectionChanged(g); };
            gv.OpenRequested += OpenDetail;
            gv.FilterRequested += () => OpenFilters(gv);
            gv.SortRequested += () => OpenSort(gv);
            gv.HintsChanged += () => { if (gv == CurrentGames && _overlays.Count == 0) UpdateHints(); };
        }

        Dock.AppRequested += w =>
        {
            CloseFlyouts();
            WindowsShell.Activate(w.Handle);
        };
        Dock.MinimizeRequested += w => { WindowsShell.Minimize(w.Handle); _ = RefreshDockAsync(); };
        Dock.CloseRequested += w => WindowsShell.Close(w.Handle);
        Dock.WindowsMenuRequested += () => ToggleDockPanel(WindowsMenu, OpenWindowsMenu);
        Dock.BackgroundRequested += () => ToggleDockPanel(BackgroundApps, OpenBackgroundApps);
        Dock.OptionsRequested += () => ToggleDockPanel(DockOptions, OpenDockOptions);
        Dock.HintsChanged += UpdateHints;
        Dock.LeaveRequested += LeaveDock;
        Dock.PreviewGotKeyboardFocus += (_, e) =>
        {
            if (Dock.IsKeyboardFocusWithin || _keepBeforeDock) return;
            // Là où reprendre en sortant de la barre.
            _beforeDock = e.OldFocus is UIElement { IsVisible: true } old && old.IsDescendantOf(Pages) ? old : null;
        };
        Dock.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            var inDock = (bool)e.NewValue;
            // Une seule zone active : la page s'atténue et sa sélection s'efface tant que la barre a la main.
            if (!_overlays.Any(o => o is DetailView or StatsView)) Anim.Fade(Pages, inDock ? 0.45 : 1, 200);
            _installed.SetSuspended(inDock);
            _all.SetSuspended(inDock);
            UpdateHints();
        };

        _all.SetCategories(_settings.LibraryCategories);
        _all.CategoriesRequested += () => OpenCategoryPicker(_all);

        _discoverView.HeroRequested += path =>
        {
            if (_subView != _discoverView || _overlays.Count > 0) return;
            _heroPath = path;
            _heroTimer.Stop();
            _heroTimer.Start();
        };
        _explore.HeroRequested += path =>
        {
            if (CurrentView != _explore || _overlays.Count > 0) return;
            _heroPath = path;
            _heroTimer.Stop();
            _heroTimer.Start();
        };
        _explore.HintsChanged += () => { if (CurrentView == _explore && _overlays.Count == 0) UpdateHints(); };
        _explore.SearchRequested += OpenStoreSearchKeyboard;
        _explore.OpenHit += OpenSearchHit;
        _explore.OpenNew += d =>
        {
            if (d.Owned is { } owned) OpenDetail(owned);
            else OpenStoreDetail(d);
        };
        _explore.OpenTrends += OpenDiscover;
        _explore.OpenFree += g => OpenStorePrompt(g.Title, PlatformInfo.Of(Platform.Epic),
            () => GameLauncher.Open(_epicInstalled ? g.LauncherUrl : g.WebUrl));

        _discoverView.OpenGame += OpenDetail;
        _discoverView.OpenStore += OpenStorePage;
        _discoverView.OpenDetails += OpenStoreDetail;

        _achievementsView.HintsChanged += () => { if (CurrentView == _achievementsView && _overlays.Count == 0) UpdateHints(); };
        _achievementsView.IconLoader = url => _achievements.IconAsync(url, CancellationToken.None);
        _achievementsView.CoverLoader = AchievementCoverAsync;


        _settingsView.EditRequested += (title, value, apply) =>
        {
            var kb = new KeyboardPanel(title, value, "Saisis ou colle la valeur", lowercase: true);
            kb.Submitted += apply;
            OpenOverlay(kb);
        };
        _settingsView.Changed += _settings.Save;
        _settingsView.KeysChanged += () =>
        {
            Toast("", "Clé enregistrée, mise à jour de la bibliothèque…");
            _lastScan = DateTime.MinValue;
            _ = ScanAsync(initial: false);
        };
        _settingsView.RescanRequested += () => _ = ScanAsync(initial: false);
        _settingsView.RefreshMetadataRequested += () =>
        {
            _enrichCts?.Cancel();
            foreach (var g in _games) _metadata.Forget(g);
            Toast("", "Fiches effacées, nouveau téléchargement en cours");
            _ = EnrichAsync(_games);
        };
        _settingsView.MissingMetadataRequested += () =>
        {
            var missing = _games.Count(g => g.CoverPath is null || g.HeroPath is null || g.LogoPath is null);
            Toast("", missing == 0 ? "Toutes les fiches ont déjà leurs visuels" : $"Recherche des visuels manquants pour {missing} jeu{(missing > 1 ? "x" : "")}…");
            if (missing > 0) _ = EnrichAsync(_games, retryMissing: true);
        };
        _settingsView.AppUpdateRequested += () => _ = ManualAppUpdateAsync();
        _settingsView.AccentRequested += OpenAccentPicker;
        _settingsView.ThemeRequested += OpenThemePicker;
        _settingsView.UiScaleRequested += OpenUiScalePicker;
        _settingsView.HiddenGamesRequested += OpenHiddenGames;
    }

    private void OnSelectionChanged(Game? game)
    {
        _heroPath = game?.HeroPath ?? game?.CoverPath;
        _heroTimer.Stop();
        _heroTimer.Start();
        if (game is not null)
        {
            // La fiche peut arriver après la sélection : on suit les changements de visuel.
            game.PropertyChanged -= SelectedGameChanged;
            game.PropertyChanged += SelectedGameChanged;
        }
    }

    private void SelectedGameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Game g || e.PropertyName is not (nameof(Game.HeroPath) or nameof(Game.CoverPath))) return;
        if (CurrentGames?.Selected != g) { g.PropertyChanged -= SelectedGameChanged; return; }
        Dispatcher.BeginInvoke(() => OnSelectionChanged(g));
    }

    // =====================================================================
    // Fond animé
    // =====================================================================

    private void SetHero(string? path)
    {
        _heroPath = path;
        _heroTimer.Stop();
        ShowHero();
    }

    private string? _shownHero;

    private async void ShowHero()
    {
        var path = _heroPath;
        if (path == _shownHero) return;
        _shownHero = path;

        if (path is null)
        {
            FadeOutHeroes(); // pas de visuel : le motif réapparaît
            Anim.Fade(Pattern, 1, HeroFadeMs);
            return;
        }

        var bmp = await ImageLoader.LoadAsync(path, 1920);
        if (_heroPath != path) return;
        if (bmp is null)
        {
            FadeOutHeroes();
            Anim.Fade(Pattern, 1, HeroFadeMs);
            return;
        }

        // Un visuel de jeu occupe le fond : le motif s'efface.
        Anim.Fade(Pattern, 0, HeroFadeMs);
        // Une jaquette (portrait) sert de repli : floutée, elle fait une ambiance colorée.
        var isCover = CurrentGames?.Selected is { HeroPath: null };
        FadeOutHeroes();
        // Chaque visuel a son calque : changer de jeu pendant un fondu ne fait jamais sauter l'image.
        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.UniformToFill,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
            Effect = isCover ? new BlurEffect { Radius = 60, RenderingBias = RenderingBias.Performance } : null,
        };
        HeroHost.Children.Add(image);
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, isCover ? 0.45 : 0.9, Anim.Ms(HeroFadeMs)) { EasingFunction = Anim.InOut });
    }

    /// <summary>Durée du fondu entre deux visuels de fond.</summary>
    private const double HeroFadeMs = 1600;

    /// <summary>Les visuels affichés s'effacent un peu après le début du suivant, puis leur calque est retiré.</summary>
    private void FadeOutHeroes()
    {
        foreach (var old in HeroHost.Children.OfType<Image>().Where(i => i.Tag is null).ToList())
        {
            old.Tag = "sortant";
            var fade = new DoubleAnimation(0, Anim.Ms(HeroFadeMs * 0.7)) { BeginTime = TimeSpan.FromMilliseconds(HeroFadeMs * 0.3), EasingFunction = Anim.InOut };
            fade.Completed += (_, _) => HeroHost.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }
    }

    // =====================================================================
    // Entrées : manette et clavier
    // =====================================================================

    private void OnNav(NavAction action)
    {
        UiSounds.OnNav(action, onKeyboard: _overlays.LastOrDefault() is KeyboardPanel);
        if (LaunchSplash.Visibility == Visibility.Visible)
        {
            if (action == NavAction.Back) HideLaunchSplash();
            return;
        }

        // Select : la sélection passe sur le bouton du menu Windows (les volets ouverts se ferment).
        if (action == NavAction.View && _overlays.All(o => o is not (DetailView or StatsView or WebPreview)))
        {
            var fromDockPanel = _dockPanel is not null;
            CloseFlyouts();
            // Déjà sur ce bouton : Select ressort de la barre.
            if (Dock.IsOnMenu && !fromDockPanel) LeaveDock();
            else Dock.Enter();
            UpdateHints();
            return;
        }

        if (_overlays.Count > 0)
        {
            // Start referme le menu système.
            if (action == NavAction.Menu && _overlays[^1] == _systemMenu) _systemMenu.Close();
            else _overlays[^1].Handle(action);
            return;
        }

        if (Dock.IsKeyboardFocusWithin && action is not (NavAction.Menu or NavAction.PrevTab or NavAction.NextTab) && Dock.Handle(action)) return;
        // Quitter un onglet ou ouvrir le menu fait sortir de la barre.
        if (Dock.IsKeyboardFocusWithin && action is NavAction.Menu or NavAction.PrevTab or NavAction.NextTab) FocusSink.Focus();

        switch (action)
        {
            case NavAction.Menu:
                OpenSystemMenu();
                return;
            case NavAction.PrevTab:
                ShowPage(_page - 1);
                return;
            case NavAction.NextTab:
                ShowPage(_page + 1);
                return;
        }

        var handled = CurrentView switch
        {
            GamesView gv => gv.Handle(action),
            DiscoverView dv => dv.Handle(action),
            ExploreView ev => ev.Handle(action),
            PlatformsView pv => pv.Handle(action),
            AchievementsView av => av.Handle(action),
            SettingsView sv => sv.Handle(action),
            _ => false,
        };

        // Au bord gauche d'une page : la barre, juste à côté.
        if (!handled && action == NavAction.Left)
        {
            Dock.Enter();
            return;
        }

        if (!handled && action == NavAction.Back && _subView is not null)
        {
            CloseSubPage();
            return;
        }

        // B sur une liste filtrée : on retire les filtres.
        if (!handled && action == NavAction.Back && CurrentGames is { Filter.IsActive: true } g)
        {
            g.Filter.Reset();
            g.Refresh(toTop: true);
            Toast("", "Filtres retirés");
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var typing = _overlays.LastOrDefault() is KeyboardPanel;
        NavAction? a = e.Key switch
        {
            Key.Left => NavAction.Left,
            Key.Right => NavAction.Right,
            Key.Up => NavAction.Up,
            Key.Down => NavAction.Down,
            Key.Enter => typing ? NavAction.Menu : NavAction.Accept,
            // Échap ouvre le menu Start quand il n'y a rien à refermer.
            Key.Escape => _overlays.Count == 0 && _subView is null ? NavAction.Menu : NavAction.Back,
            Key.Back => typing ? NavAction.Option : NavAction.Back,
            Key.F2 => NavAction.View,
            Key.Tab => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? NavAction.PrevTab : NavAction.NextTab,
            Key.PageUp => NavAction.PrevLetter,
            Key.PageDown => NavAction.NextLetter,
            Key.F when !typing => NavAction.Option,
            Key.T when !typing => NavAction.Menu,
            Key.Space when !typing => NavAction.Secondary,
            Key.F4 when Keyboard.Modifiers == ModifierKeys.Alt => null,
            _ => null,
        };
        if (a is null) return;
        e.Handled = true;
        OnNav(a.Value);
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_overlays.LastOrDefault() is not KeyboardPanel kb || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        kb.Type(e.Text);
        e.Handled = true;
    }

    // =====================================================================
    // Panneaux superposés
    // =====================================================================

    private void OpenOverlay(OverlayBase overlay)
    {
        overlay.CloseRequested += CloseOverlay;
        overlay.HintsChanged += UpdateHints;
        _overlays.Add(overlay);
        OverlayHost.Children.Add(overlay);
        overlay.OnOpened();
        if (overlay is KeyboardPanel) SyncTopBar();
        // Pages plein écran (fiche, statistiques) : la page et la barre d'onglets s'effacent dessous.
        if (overlay is DetailView or StatsView)
        {
            Anim.Fade(Pages, 0, 250);
            SyncTopBar();
        }
        UpdateHints();
    }

    private void CloseOverlay(OverlayBase overlay)
    {
        if (!_overlays.Remove(overlay)) return;
        var fromDock = overlay == _dockPanel;
        if (fromDock) _dockPanel = null;
        overlay.IsHitTestVisible = false;
        Anim.Exit(overlay, 30, 0, 180, () => OverlayHost.Children.Remove(overlay));
        if (overlay is KeyboardPanel) SyncTopBar();
        if (overlay is DetailView or StatsView && !_overlays.Any(o => o is DetailView or StatsView))
        {
            Anim.Fade(Pages, 1, 300);
            SyncTopBar();
        }
        UpdateHints();

        if (_overlays.Count == 0)
        {
            // Le focus revient à la fenêtre pour que le clavier continue de répondre.
            Focus();
            // Panneau ouvert depuis la barre de gauche : la sélection y retourne.
            if (fromDock)
            {
                _keepBeforeDock = true;
                Dock.Enter();
                _keepBeforeDock = false;
                return;
            }
            switch (CurrentView)
            {
                case DiscoverView dv: dv.FocusFirst(); SetHero(dv.CurrentHero); break;
                case ExploreView ev: ev.FocusFirst(); SetHero(ev.CurrentHero); break;
                case PlatformsView pv: pv.FocusFirst(); break;
                case AchievementsView av: av.FocusFirst(); SetHero(null); break;
                case SettingsView sv: sv.FocusFirst(); break;
                case GamesView gv: OnSelectionChanged(gv.Selected); gv.RestoreFocus(); break;
            }
        }
    }

    private void OpenDetail(Game game)
    {
        var detail = new DetailView();
        detail.SetGame(game);
        detail.PlayRequested += Launch;
        detail.FolderRequested += g => GameLauncher.OpenFolder(g);
        detail.UninstallRequested += g =>
        {
            // Confirmation avant de lancer le désinstalleur (Steam, programme du jeu ou paramètres Windows).
            var confirm = new SidePanel("Désinstaller ?", g.DisplayTitle);
            confirm.SetHints("A:Valider|B:Annuler");
            confirm.AddChoice("Oui, désinstaller", false, () =>
            {
                confirm.Close();
                if (GameLauncher.Uninstall(g)) Toast("", $"Désinstallation de {g.DisplayTitle} lancée");
                else Toast("", "Impossible de lancer la désinstallation");
            });
            confirm.AddChoice("Annuler", false, confirm.Close);
            OpenOverlay(confirm);
        };
        detail.PlatformRequested += g => OpenStorePrompt(g.DisplayTitle, g.PlatformInfo, () => GameLauncher.OpenInPlatform(g));
        detail.HideRequested += g =>
        {
            g.IsHidden = !g.IsHidden;
            _library.SaveUserData(g);
            Toast(g.IsHidden ? "" : "", g.IsHidden ? "Jeu masqué (filtre « Inclure les masqués » pour le revoir)" : "Jeu de nouveau visible");
            detail.Close();
            _installed.Refresh();
            _all.Refresh();
        };
        detail.RefreshRequested += async g =>
        {
            _metadata.Forget(g);
            Toast("", "Actualisation de la fiche…");
            await _metadata.EnrichAsync([g], null, CancellationToken.None);
            detail.SetGame(g);
            OnSelectionChanged(g);
        };
        detail.AchievementsRequested += OpenGameAchievements;
        OpenOverlay(detail);
        AttachAchievements(detail);
    }

    /// <summary>Succès mis en avant dans la fiche (déjà ouverte si les succès arrivent après).</summary>
    private void AttachAchievements(DetailView detail)
    {
        if (detail.Game is { } game)
            detail.SetAchievements(_achievementList.FirstOrDefault(a => a.Game?.Id == game.Id), url => _achievements.IconAsync(url, CancellationToken.None));
    }

    private void RefreshDetailAchievements()
    {
        foreach (var detail in _overlays.OfType<DetailView>()) AttachAchievements(detail);
    }

    /// <summary>« Voir plus » depuis une fiche : onglet Succès, sur ce jeu.</summary>
    private void OpenGameAchievements(AchievementGame game)
    {
        foreach (var overlay in _overlays.ToList()) overlay.Close();
        ShowPage(Array.FindIndex(_pages, p => p.View == _achievementsView));
        _achievementsView.ShowGame(game);
    }

    /// <summary>Bouton + : toutes les catégories et tous les tags, on coche les 8 à afficher.</summary>
    private void OpenCategoryPicker(GamesView view)
    {
        var chosen = _settings.LibraryCategories;
        var panel = new SidePanel("Catégories");
        panel.SetHints("A:Cocher|B:Fermer");
        void Subtitle() => panel.SetSubtitle($"{chosen.Count} / {GameCategories.MaxButtons} boutons affichés");

        void Add(string key, string label, int count)
        {
            System.Windows.Controls.Primitives.ToggleButton? toggle = null;
            toggle = panel.AddToggle(label, chosen.Contains(key), on =>
            {
                if (on && chosen.Count >= GameCategories.MaxButtons)
                {
                    toggle!.IsChecked = false;
                    Toast("", $"{GameCategories.MaxButtons} boutons maximum : décoches-en un d'abord");
                    return;
                }
                if (on) chosen.Add(key);
                else chosen.Remove(key);
                _settings.Save();
                Subtitle();
                view.SetCategories(chosen);
                if (!on && view.Filter.Category == key)
                {
                    view.Filter.Category = null;
                    view.Refresh(toTop: true);
                }
            }, detail: count.ToString());
        }

        Subtitle();
        panel.AddHeader("Catégories");
        foreach (var c in GameCategories.Curated) Add(c.Key, c.Label, view.Source.Count(g => GameCategories.Matches(g, c.Key)));
        panel.AddHeader("Tous les tags Steam de ta bibliothèque");
        foreach (var (tag, count) in GameCategories.TagsOf(view.Source)) Add(GameCategories.KeyOfTag(tag), _tags.Label(tag), count);
        OpenOverlay(panel);
    }

    private void OpenFilters(GamesView view)
    {
        var panel = new SidePanel("Filtres", $"{view.Source.Count} jeux dans cet onglet");
        panel.SetHints("A:Cocher|B:Fermer");
        void Changed()
        {
            view.Refresh(toTop: true);
        }

        void Build()
        {
            panel.Clear();
            var f = view.Filter;

            panel.AddChoice("Rechercher", false, () =>
            {
                panel.Close();
                OpenSearch(view);
            }, detail: string.IsNullOrWhiteSpace(f.Search) ? null : $"« {f.Search} »");
            panel.AddChoice("Réinitialiser les filtres", false, () =>
            {
                f.Reset();
                Changed();
                Build();
                FocusNav.FocusFirst(panel);
            });

            if (!view.InstalledOnly)
            {
                panel.AddHeader("Statut");
                var toggles = new List<(InstallFilter Value, System.Windows.Controls.Primitives.ToggleButton Button)>();
                foreach (var (value, label) in new[] { (InstallFilter.All, "Tous"), (InstallFilter.Installed, "Installés"), (InstallFilter.NotInstalled, "Non installés") })
                {
                    var t = panel.AddToggle(label, f.Install == value, _ =>
                    {
                        f.Install = value;
                        foreach (var (v, b) in toggles) b.IsChecked = v == value;
                        Changed();
                    });
                    toggles.Add((value, t));
                }
            }

            panel.AddHeader("Afficher");
            panel.AddToggle("Inclure les jeux masqués", f.ShowHidden, v => { f.ShowHidden = v; Changed(); }, asSwitch: true);

            var platforms = view.Source.GroupBy(g => g.Platform).OrderByDescending(g => g.Count()).ToList();
            if (platforms.Count > 1)
            {
                panel.AddHeader("Plateformes");
                // « Toutes les plateformes » vide la sélection ; cocher une plateforme le décoche.
                var buttons = new List<(Platform Value, System.Windows.Controls.Primitives.ToggleButton Button)>();
                System.Windows.Controls.Primitives.ToggleButton? all = null;
                all = panel.AddToggle("Toutes les plateformes", f.Platforms.Count == 0, _ =>
                {
                    f.Platforms.Clear();
                    foreach (var (_, b) in buttons) b.IsChecked = false;
                    all!.IsChecked = true;
                    Changed();
                }, detail: view.Source.Count.ToString());
                foreach (var p in platforms)
                {
                    var t = panel.AddToggle(PlatformInfo.Of(p.Key).Name, f.Platforms.Contains(p.Key), v =>
                    {
                        if (v) f.Platforms.Add(p.Key); else f.Platforms.Remove(p.Key);
                        all.IsChecked = f.Platforms.Count == 0;
                        Changed();
                    }, detail: p.Count().ToString());
                    buttons.Add((p.Key, t));
                }
            }

            var genres = view.Source.SelectMany(g => g.Meta?.Genres ?? []).GroupBy(x => x).OrderByDescending(x => x.Count()).Take(16).ToList();
            if (genres.Count > 0)
            {
                panel.AddHeader("Genre");
                var toggles = new List<(string? Value, System.Windows.Controls.Primitives.ToggleButton Button)>();
                foreach (var (value, label, count) in new[] { ((string?)null, "Tous les genres", (int?)null) }
                             .Concat(genres.Select(g => ((string?)g.Key, g.Key, (int?)g.Count()))))
                {
                    var t = panel.AddToggle(label, f.Genre == value, _ =>
                    {
                        f.Genre = value;
                        foreach (var (v, b) in toggles) b.IsChecked = v == value;
                        Changed();
                    }, detail: count?.ToString());
                    toggles.Add((value, t));
                }
            }
        }

        Build();
        OpenOverlay(panel);
    }

    // ---- Échelle de l'interface ----

    public static readonly IReadOnlyList<(double Value, string Label)> UiScales =
    [
        (0, "Automatique"), (0.75, "75 %"), (0.84, "84 %"), (0.92, "92 %"), (1.0, "100 %"), (1.1, "110 %"),
    ];

    /// <summary>Échelle automatique : réduite sur un petit écran (ROG Ally en 1920×1080 à 150 % ≈ 672 de haut).</summary>
    private double AutoUiScale => ActualHeight is > 0 and < 800 ? 0.84 : 1.0;

    private double EffectiveUiScale(double setting) => setting > 0 ? setting : AutoUiScale;

    /// <summary>Applique l'échelle à toute l'interface (mise en page recalculée, texte net).</summary>
    private void ApplyUiScale(double? preview = null)
    {
        var s = EffectiveUiScale(preview ?? _settings.UiScale);
        if (RootGrid.LayoutTransform is ScaleTransform current && Math.Abs(current.ScaleX - s) < 0.001) return;
        RootGrid.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }

    public string UiScaleLabel(double setting)
        => setting > 0 ? $"{setting:P0}" : $"Automatique ({AutoUiScale:P0})";

    /// <summary>Choix de la taille de l'interface : aperçu en direct, validé avec A, annulé avec B.</summary>
    private void OpenUiScalePicker()
    {
        var panel = new SidePanel("Taille de l'interface", "Aperçu en direct · A pour valider");
        foreach (var (value, label) in UiScales)
        {
            var isCurrent = Math.Abs(value - _settings.UiScale) < 0.001;
            var text = value == 0 ? UiScaleLabel(0) : label;
            var b = panel.AddChoice(text, isCurrent, () =>
            {
                _settings.UiScale = value;
                _settings.Save();
                ApplyUiScale();
                _settingsView.UpdateUiScale(UiScaleLabel(value));
                panel.Close();
            });
            b.GotKeyboardFocus += (_, _) => ApplyUiScale(value);
            if (isCurrent) panel.InitialFocus = b;
        }
        panel.CloseRequested += _ => ApplyUiScale();
        OpenOverlay(panel);
    }

    /// <summary>Choix de la couleur d'accent : aperçu en direct au survol, validé avec A, annulé avec B.</summary>
    private void OpenThemePicker()
    {
        var panel = new SidePanel("Thème", "Aperçu en direct · A pour valider");
        foreach (var theme in AppTheme.Choices)
        {
            var isCurrent = theme.Id == AppTheme.Find(_settings.Theme).Id;
            var b = panel.AddChoice(theme.Name, isCurrent, () =>
            {
                _settings.Theme = theme.Id;
                _settings.Save();
                AppTheme.Apply(theme.Id);
                _settingsView.UpdateTheme();
                panel.Close();
            }, swatch: AppTheme.Swatch(theme));
            b.GotKeyboardFocus += (_, _) => AppTheme.Apply(theme.Id);
            if (isCurrent) panel.InitialFocus = b;
        }
        // Fermé sans valider : on revient au thème enregistré.
        panel.CloseRequested += _ => AppTheme.Apply(_settings.Theme);
        OpenOverlay(panel);
    }

    private void OpenAccentPicker()
    {
        var panel = new SidePanel("Couleur d'accent", "Aperçu en direct · A pour valider");
        foreach (var (name, hex) in AccentTheme.Choices)
        {
            var isCurrent = string.Equals(hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            var b = panel.AddChoice(name, isCurrent, () =>
            {
                _settings.AccentColor = hex;
                _settings.Save();
                AccentTheme.Apply(hex);
                _settingsView.UpdateAccent();
                panel.Close();
            }, swatch: AccentTheme.Parse(hex));
            b.GotKeyboardFocus += (_, _) => AccentTheme.Apply(hex);
            if (isCurrent) panel.InitialFocus = b;
        }
        // Fermé sans valider : on revient à la couleur enregistrée.
        panel.CloseRequested += _ => AccentTheme.Apply(_settings.AccentColor);
        OpenOverlay(panel);
    }

    /// <summary>Liste des jeux masqués ; A rend le jeu de nouveau visible.</summary>
    private void OpenHiddenGames()
    {
        var panel = new SidePanel("Jeux masqués");
        panel.SetHints("A:Rendre visible|B:Fermer");

        void Build()
        {
            panel.Clear();
            var hidden = _games.Where(g => g.IsHidden).OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
            panel.SetSubtitle(hidden.Count == 0 ? "" : $"{hidden.Count} jeu{(hidden.Count > 1 ? "x" : "")} · A pour rendre visible");
            if (hidden.Count == 0)
            {
                panel.AddHeader("Aucun jeu masqué");
                return;
            }
            foreach (var g in hidden)
            {
                panel.AddChoice(g.DisplayTitle, false, () =>
                {
                    g.IsHidden = false;
                    _library.SaveUserData(g);
                    _installed.Refresh();
                    _all.Refresh();
                    Toast("", $"{g.DisplayTitle} est de nouveau visible");
                    Build();
                    Dispatcher.BeginInvoke(() => FocusNav.FocusFirst(panel), DispatcherPriority.Input);
                }, detail: g.PlatformInfo.Name);
            }
        }

        Build();
        OpenOverlay(panel);
    }

    private void OpenStats()
    {
        var stats = new StatsView(_games, _freeGames, _achievementList);
        stats.DiskProgress += (detail, progress) =>
        {
            if (detail is null) EndActivity("disk");
            else Activity("disk", "Calcul de l'espace disque", detail, progress);
        };
        OpenOverlay(stats);
    }

    private void OpenSort(GamesView view)
    {
        var panel = new SidePanel("Trier par");
        foreach (var (mode, label) in GameFilter.SortModes)
        {
            var b = panel.AddChoice(label, view.Filter.Sort == mode, () =>
            {
                view.Filter.Sort = mode;
                _settings.Sorts[_pages.First(p => p.View == view).Key] = mode;
                _settings.Save();
                view.Refresh(toTop: true);
                panel.Close();
            });
            if (view.Filter.Sort == mode) panel.InitialFocus = b;
        }
        OpenOverlay(panel);
    }

    // ---- Barre de gauche ----

    private const string WindowsMenu = "Windows", BackgroundApps = "Applications en arrière-plan", DockOptions = "Options";
    private SidePanel? _dockPanel;
    private string? _dockKind;
    private bool _refreshingDock;

    /// <summary>
    /// Un seul volet à la fois : ouvrir un volet de la barre ferme celui qui est affiché, et recliquer sur le même le referme.
    /// Les pages plein écran (fiche d'un jeu, statistiques…) restent ouvertes dessous.
    /// </summary>
    private void ToggleDockPanel(string kind, Action open)
    {
        var same = _dockPanel is not null && _dockKind == kind;
        CloseFlyouts(instantly: !same);
        if (!same) open();
    }

    /// <summary>Ferme les volets ; <paramref name="instantly"/> : sans fondu, quand un autre prend sa place.</summary>
    private void CloseFlyouts(bool instantly = false)
    {
        foreach (var overlay in _overlays.Where(o => o is not (DetailView or StatsView or WebPreview)).ToList())
        {
            overlay.Close();
            if (instantly) OverlayHost.Children.Remove(overlay);
        }
    }

    private async Task RefreshDockAsync()
    {
        if (_refreshingDock) return;
        _refreshingDock = true;
        try
        {
            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var (windows, background) = await Task.Run(() =>
            {
                var open = WindowsShell.Enumerate(self);
                return (open, WindowsShell.ListBackgroundApps(open));
            });
            Dock.SetWindows(windows, background);
        }
        catch (Exception ex) { Log.Error("Barre de gauche", ex); }
        finally { _refreshingDock = false; }
    }

    private UIElement? _beforeDock;
    private bool _keepBeforeDock;

    /// <summary>Sortie de la barre : la sélection reprend exactement sa place dans la page.</summary>
    private void LeaveDock()
    {
        FocusSink.Focus();
        if (_beforeDock is { IsVisible: true } previous && previous.IsDescendantOf(CurrentView)) FocusNav.Focus(previous);
        else if (CurrentView is GamesView games) games.RestoreFocus();
        else OnViewShown(CurrentView);
        UpdateHints();
    }

    private SidePanel DockPanelOf(string title, bool bottom)
    {
        var panel = new SidePanel(title);
        panel.AnchorLeft(bottom);
        panel.SetHints("A:Ouvrir|B:Fermer");
        _dockPanel = panel;
        _dockKind = title;
        return panel;
    }

    /// <summary>Applications lancées sans fenêtre (Steam, Epic Games…) : A fait réapparaître leur fenêtre.</summary>
    private void OpenBackgroundApps()
    {
        var panel = DockPanelOf(BackgroundApps, bottom: true);
        if (Dock.BackgroundApps.Count == 0) panel.AddHeader("Aucune application en arrière-plan");
        foreach (var app in Dock.BackgroundApps)
            panel.AddApp(app.Name, app.Path, () =>
            {
                panel.Close();
                if (!GameLauncher.Open(app.OpenTarget)) Toast("", $"Impossible d'ouvrir {app.Name}");
            });
        OpenOverlay(panel);
    }

    /// <summary>Options : volets de Windows, clavier tactile, bureau.</summary>
    private void OpenDockOptions()
    {
        var panel = DockPanelOf(DockOptions, bottom: true);
        void Add(string glyph, string label, Action run) => panel.AddApp(label, null, () =>
        {
            panel.Close();
            run();
        }, glyph);
        Add("", "Wi-Fi, Bluetooth, volume et luminosité", WindowsShell.ShowQuickSettings);
        Add("", "Notifications", WindowsShell.ShowNotifications);
        Add("", "Clavier de Windows", () => _ = WindowsShell.ToggleTouchKeyboardAsync());
        Add("", "Bureau de Windows", WindowsShell.ShowDesktop);
        OpenOverlay(panel);
    }

    /// <summary>Menu Windows maison : accès rapides, puis recherche et liste de toutes les applications.</summary>
    private void OpenWindowsMenu()
    {
        var panel = DockPanelOf(WindowsMenu, bottom: false);
        List<InstalledApp>? apps = null;
        List<PlatformInfo> launchers = [];
        List<InstalledApp> recent = [];
        var search = "";
        var browser = WindowsShell.DefaultBrowser();

        void Quick(string glyph, string label, Action run) => panel.AddApp(label, null, () =>
        {
            panel.Close();
            run();
        }, glyph);

        void Build()
        {
            panel.Clear();
            panel.AddApp(string.IsNullOrEmpty(search) ? "Rechercher une application" : $"« {search} »", null, () =>
            {
                var keyboard = new KeyboardPanel("Rechercher une application", search, "Nom de l'application…");
                keyboard.ValueChanged += v =>
                {
                    search = v;
                    Build();
                };
                keyboard.CloseRequested += _ => Dispatcher.BeginInvoke(() => FocusNav.FocusFirst(panel), DispatcherPriority.Input);
                OpenOverlay(keyboard);
            }, "");
            if (search.Length == 0)
            {
                panel.AddHeader("Accès rapide");
                panel.AddApp("Navigateur Web", browser, () =>
                {
                    panel.Close();
                    var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (!WindowsShell.ShowBrowser(browser, self)) Toast("", "Impossible d'ouvrir le navigateur");
                }, browser is null ? "" : null);
                Quick("", "Paramètres", () => GameLauncher.Open("ms-settings:"));
                Quick("", "Explorateur de fichiers", () => GameLauncher.Open("explorer.exe"));
                Quick("", "Gestionnaire des tâches", () => GameLauncher.Open("taskmgr.exe"));
                Quick("", "Menu Démarrer de Windows", WindowsShell.ShowStartMenu);
            }
            if (search.Length == 0 && launchers.Count > 0)
            {
                panel.AddHeader("Lanceurs de jeux");
                panel.AddIconGrid(4, launchers.Select(l => (l.Logo, l.Name, (Action)(() =>
                {
                    panel.Close();
                    if (!GameLauncher.Open(l.LauncherUri ?? LauncherWarmup.Command(l) ?? "")) Toast("", $"Impossible d'ouvrir {l.Name}");
                }))));
            }
            if (search.Length == 0 && recent.Count > 0)
            {
                panel.AddHeader("Dernières applications");
                foreach (var app in recent) AddInstalledApp(app);
            }
            panel.AddHeader("Applications");
            if (apps is null)
            {
                panel.AddHeader("Chargement…");
                return;
            }
            var words = Text.Normalize(search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var app in apps.Where(a => words.All(Text.Normalize(a.Name).Contains))) AddInstalledApp(app);
        }

        void AddInstalledApp(InstalledApp app) => panel.AddApp(app.Name, app.IconSource, () =>
        {
            panel.Close();
            if (!WindowsShell.LaunchApp(app))
            {
                Toast("", $"Impossible d'ouvrir {app.Name}");
                return;
            }
            _settings.RecentApps[app.Id] = DateTime.Now;
            _settings.Save();
        });

        Build();
        OpenOverlay(panel);
        LoadApps();

        async void LoadApps()
        {
            apps = await WindowsShell.ListAppsAsync();
            var loaded = apps;
            (launchers, recent) = await Task.Run(() =>
            {
                // Historique de Windows, complété par ce qui a été lancé depuis ce menu.
                var history = WindowsShell.WindowsLaunchHistory();
                foreach (var (id, when) in _settings.RecentApps)
                    if (!history.TryGetValue(id, out var known) || when > known) history[id] = when;
                var latest = loaded.Where(a => history.ContainsKey(a.Id) && !WindowsShell.IsGameLauncher(a.Name))
                    .OrderByDescending(a => history[a.Id]).Take(5).ToList();
                return (LibraryService.InstalledLaunchers(), latest);
            });
            if (_overlays.LastOrDefault() != panel) return;
            Build();
            FocusNav.FocusFirst(panel);
        }
    }

    private MainMenu? _systemMenu;

    /// <summary>Bouton Start (ou Échap) : menu principal à droite.</summary>
    private void OpenSystemMenu()
    {
        var menu = new MainMenu();
        // Mises à jour : le menu affiche le dernier résultat, tenu à jour en arrière-plan (voir RefreshDue).
        var (winText, winColor) = MainMenu.WindowsStatus(_windowsUpdates);
        var (asusText, asusColor) = MainMenu.AsusStatus(_asusApp, _asusApp is not null || _lastUpdateCheck != default);

        menu.AddSection("Mises à jour");
        var updates = new List<MenuTile>
        {
            new("\uE895", "Windows", winText, () =>
            {
                menu.Close();
                _updatesDirty = true;
                GameLauncher.Open("ms-settings:windowsupdate");
            }, winColor),
        };
        // Armoury Crate seulement s'il est installé sur cette machine.
        if (_asusApp is { } asus)
            updates.Add(new MenuTile("\uE7FC", "Armoury Crate", asusText, async () =>
            {
                menu.Close();
                _updatesDirty = true;
                if (!await UpdateChecker.LaunchAsync(asus)) Toast("\uE783", "Impossible d'ouvrir Armoury Crate");
            }, asusColor));
        menu.AddTiles([.. updates]);
        var (appText, appColor) = AppUpdateStatus;
        menu.AddTiles(new MenuTile("", AppTile, appText, () =>
        {
            menu.Close();
            _ = ManualAppUpdateAsync();
        }, appColor));

        menu.AddSection("Accès rapide");
        menu.AddButton("\uE713", "Réglages", () =>
        {
            menu.Close();
            OpenSubPage(_settingsView);
        });
        menu.AddButton("\uE9D2", "Statistiques", () =>
        {
            menu.Close();
            OpenStats();
        });
        menu.AddButton("", "Plateformes", () =>
        {
            menu.Close();
            OpenSubPage(_platforms);
        });

        menu.AddDrives();

        menu.AddSection("Alimentation");
        menu.AddPowerButtons(
            ("\uE708", "Mettre en veille", Color.FromRgb(0x5B, 0x5F, 0xE8), () =>
            {
                menu.Close();
                if (!Power.Sleep()) Toast("\uE7BA", "Impossible de mettre l'appareil en veille");
            }),
            ("\uE7E8", "Éteindre", Color.FromRgb(0xE5, 0x39, 0x4B), () =>
            {
                menu.Close();
                var confirm = new SidePanel("Éteindre ?", "L'appareil va s'arrêter maintenant");
                confirm.SetHints("A:Valider|B:Annuler");
                confirm.AddChoice("Oui, éteindre", false, () =>
                {
                    confirm.Close();
                    if (!Power.Shutdown()) Toast("\uE7BA", "Impossible d'éteindre l'appareil");
                });
                confirm.AddChoice("Annuler", false, confirm.Close);
                OpenOverlay(confirm);
            }));

        menu.CloseRequested += _ => _systemMenu = null;
        _systemMenu = menu;
        OpenOverlay(menu);
    }

    // ---- Recherche sur toutes les boutiques (onglet Découverte) ----

    private void OpenStoreSearchKeyboard()
    {
        var kb = new KeyboardPanel("Rechercher un jeu", _explore.Query, "Nom du jeu…");
        kb.Submitted += q => _ = SearchStoresAsync(q);
        OpenOverlay(kb);
    }

    /// <summary>Bibliothèque tout de suite, puis Steam, puis GOG : les résultats s'ajoutent au fil des réponses.</summary>
    private async Task SearchStoresAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        query = query.Trim();
        _explore.BeginSearch(query);
        UpdateHints();
        if (query.Length == 0) return;

        var words = Text.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var library = _games.Where(g => !g.IsHidden && words.All(Text.Normalize(g.DisplayTitle).Contains))
            .OrderByDescending(g => g.IsInstalled).ThenBy(g => g.DisplayTitle.Length).Take(10).ToList();
        var hits = library.Select(g => new SearchHit { Title = g.DisplayTitle, Platform = g.PlatformInfo, Owned = g, CoverPath = g.CoverPath, HeroPath = g.HeroPath }).ToList();
        var focusOnResults = true;
        void Show(bool done)
        {
            _explore.SetResults(query, hits.ToList(), done);
            // Une seule fois, au premier résultat : la sélection quitte la barre de recherche.
            if (focusOnResults && hits.Count > 0 && _overlays.Count == 0 && CurrentView == _explore)
            {
                focusOnResults = false;
                _explore.FocusResults();
            }
            UpdateHints();
        }
        if (hits.Count > 0) Show(done: false);

        Activity("search", "Recherche", $"« {query} » sur Steam et GOG…");
        try
        {
            var steamTask = _search.SteamAsync(query, cts.Token);
            var gogTask = _search.GogAsync(query, cts.Token);

            // Jeux Steam déjà possédés : ils figurent déjà parmi les résultats de la bibliothèque.
            var ownedSteam = _games.Select(g => g.SteamAppId ?? (g.Platform == Platform.Steam && int.TryParse(g.PlatformId, out var id) ? id : null))
                .OfType<int>().ToHashSet();
            var steam = (await steamTask).Where(d => !ownedSteam.Contains(d.AppId)).ToList();
            if (cts.IsCancellationRequested) return;
            DiscoverService.ApplyCachedImages(steam);
            var steamHits = steam.Select(d => new SearchHit
            {
                Title = d.Name,
                Platform = PlatformInfo.Of(Platform.Steam),
                Steam = d,
                Price = d.PriceText is { } p ? (d.DiscountText is { } off ? $"{off}  {p}" : p) : d.ComingSoon ? "Bientôt" : null,
                IsDiscounted = d.IsDiscounted,
                CoverPath = d.CoverPath,
                HeroPath = d.HeroPath,
            }).ToList();
            hits.AddRange(steamHits);
            Show(done: false);

            var ownedTitles = library.Select(g => Text.Normalize(g.DisplayTitle)).ToHashSet();
            var gog = (await gogTask).Where(h => !ownedTitles.Contains(Text.Normalize(h.Title))).ToList();
            if (cts.IsCancellationRequested) return;
            hits.AddRange(gog);
            Show(done: true);

            // Jaquettes Steam manquantes, téléchargées après coup.
            await _discover.LoadImagesAsync(steam, cts.Token);
            foreach (var h in steamHits)
            {
                h.CoverPath = h.Steam!.CoverPath;
                h.HeroPath = h.Steam.HeroPath;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Recherche « {query} »", ex);
            if (!cts.IsCancellationRequested) Show(done: true);
        }
        finally
        {
            if (_searchCts == cts) EndActivity("search");
        }
    }

    private void OpenSearchHit(SearchHit h)
    {
        if (h.Owned is { } g) OpenDetail(g);
        else if (h.Steam is { } d) OpenStoreDetail(d);
        else if (h.StoreUrl is { } url) OpenStorePrompt(h.Title, h.Platform, () => GameLauncher.Open(url));
    }

    private void OpenSearch(GamesView view)
    {
        var kb = new KeyboardPanel("Rechercher un jeu", view.Filter.Search, "Titre du jeu…");
        kb.ValueChanged += v =>
        {
            view.Filter.Search = v;
            view.Refresh(toTop: true);
        };
        // Recherche validée avec au moins un résultat.
        kb.Submitted += v => { if (!string.IsNullOrWhiteSpace(v) && view.Selected is not null) UiSounds.PlaySearch(); };
        OpenOverlay(kb);
    }

    // =====================================================================
    // Actions sur les jeux
    // =====================================================================

    private void Launch(Game game)
    {
        if (!game.IsInstalled)
        {
            _libraryDirty = true;
            if (GameLauncher.Install(game)) Toast("", $"Ouverture de {game.PlatformInfo.Name} pour l'installation");
            else Toast("", "Impossible d'ouvrir le lanceur");
            return;
        }

        if (!GameLauncher.Launch(game))
        {
            Toast("", $"Impossible de lancer {game.DisplayTitle}");
            return;
        }

        // Au retour du jeu : temps de jeu, « Continuer à jouer » et succès à jour.
        _libraryDirty = _achievementsDirty = true;
        game.LastPlayed = DateTime.Now;
        game.LaunchCount++;
        _library.SaveUserData(game);
        ShowLaunchSplash(game);
    }

    private async void ShowLaunchSplash(Game game)
    {
        LaunchTitle.Text = game.DisplayTitle;
        LaunchTitle.Visibility = game.LogoPath is null ? Visibility.Visible : Visibility.Collapsed;
        LaunchLogo.Source = game.LogoPath is null ? null : await ImageLoader.LoadAsync(game.LogoPath, 1000);
        LaunchLogo.Visibility = game.LogoPath is null ? Visibility.Collapsed : Visibility.Visible;
        LaunchSub.Text = $"Lancement via {game.PlatformInfo.Name}…";

        LaunchSplash.Visibility = Visibility.Visible;
        LaunchSplash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(300)));

        var scale = (ScaleTransform)LaunchLogo.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, Anim.Ms(700)) { EasingFunction = Anim.Spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, Anim.Ms(700)) { EasingFunction = Anim.Spring });
        ((TranslateTransform)LaunchBar.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-90, 260, Anim.Ms(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Anim.InOut });

        await Task.Delay(3500);
        if (_settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;
        HideLaunchSplash();
    }

    private void HideLaunchSplash()
    {
        if (LaunchSplash.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, Anim.Ms(400));
        fade.Completed += (_, _) =>
        {
            LaunchSplash.Visibility = Visibility.Collapsed;
            ((TranslateTransform)LaunchBar.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
        };
        LaunchSplash.BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================
    // Barre d'état, aides, notifications
    // =====================================================================

    // ---- Activités en cours : la plus récente est affichée dans la carte en bas à gauche ----

    private sealed record ActivityState(string Title, string Detail, double? Progress);
    private readonly List<(string Key, ActivityState State)> _activities = [];
    private bool _activityIndeterminate;

    /// <summary>Démarre ou met à jour une activité. <paramref name="progress"/> entre 0 et 1, ou null si inconnue.</summary>
    private void Activity(string key, string title, string detail, double? progress = null)
    {
        var i = _activities.FindIndex(a => a.Key == key);
        var state = new ActivityState(title, detail, progress);
        if (i >= 0) _activities[i] = (key, state);
        else _activities.Add((key, state));
        RenderActivity();
    }

    private void EndActivity(string key)
    {
        _activities.RemoveAll(a => a.Key == key);
        RenderActivity();
    }

    private void RenderActivity()
    {
        var rotate = (RotateTransform)ActivityIcon.RenderTransform;
        var fill = (TranslateTransform)ActivityFill.RenderTransform;
        if (_activities.Count == 0)
        {
            Anim.Exit(ActivityCard, 0, 12, 300, () =>
            {
                if (_activities.Count > 0) return;
                ActivityCard.Visibility = Visibility.Collapsed;
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                fill.BeginAnimation(TranslateTransform.XProperty, null);
                _activityIndeterminate = false;
            });
            return;
        }

        var (_, s) = _activities[^1];
        var others = _activities.Count - 1;
        ActivityTitle.Text = others > 0 ? $"{s.Title}  (+{others})" : s.Title;
        ActivityDetail.Text = s.Detail;
        ActivityPercent.Text = s.Progress is double p ? $"{p:P0}" : "";

        if (ActivityCard.Visibility != Visibility.Visible)
        {
            ActivityCard.Visibility = Visibility.Visible;
            Anim.Enter(ActivityCard, 0, 12, 300);
        }
        if (!rotate.HasAnimatedProperties)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Anim.Ms(1200)) { RepeatBehavior = RepeatBehavior.Forever });

        var width = ActivityTrack.ActualWidth > 0 ? ActivityTrack.ActualWidth : 340;
        if (s.Progress is double progress)
        {
            if (_activityIndeterminate)
            {
                fill.BeginAnimation(TranslateTransform.XProperty, null);
                fill.X = 0;
                _activityIndeterminate = false;
            }
            ActivityFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Clamp(progress, 0, 1) * width, Anim.Ms(350)) { EasingFunction = Anim.Out });
        }
        else if (!_activityIndeterminate)
        {
            // Durée inconnue : un segment glisse en boucle.
            _activityIndeterminate = true;
            ActivityFill.BeginAnimation(WidthProperty, null);
            ActivityFill.Width = width * 0.3;
            fill.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-width * 0.3, width, Anim.Ms(1300)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Anim.InOut });
        }
    }

    /// <summary>« environ 3 min restantes » à partir de la vitesse observée.</summary>
    private static string Remaining(DateTime started, int done, int total)
    {
        if (done < 3) return "estimation en cours…";
        var perItem = (DateTime.Now - started).TotalSeconds / done;
        var left = TimeSpan.FromSeconds(perItem * (total - done));
        return left.TotalMinutes < 1 ? "moins d'une minute" : $"environ {Math.Ceiling(left.TotalMinutes):0} min restantes";
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        var power = Power.Read();
        BatteryPanel.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (!power.HasBattery) return;
        BatteryIcon.Text = Power.Glyph(power);
        BatteryText.Text = $"{power.Percent} %";
        var level = power.Percent < 15 ? Color.FromRgb(0xFF, 0x3B, 0x4E)
                  : power.Percent < 30 ? Color.FromRgb(0xFF, 0x9F, 0x1C)
                  : Color.FromRgb(0x4C, 0xD9, 0x64);
        BatteryIcon.Foreground = BatteryText.Foreground = new SolidColorBrush(level);
    }

    private void UpdateHints()
    {
        var hints = _overlays.LastOrDefault()?.Hints ?? (Dock.IsKeyboardFocusWithin ? Dock.Hints : null) ?? CurrentView switch
        {
            GamesView gv => gv.Hints,
            DiscoverView => "A:Ouvrir|Y:Page Steam|↑↓:Catégorie|B:Retour",
            ExploreView ev => ev.Hints,
            PlatformsView => "Y:Ouvrir le lanceur|B:Retour",
            AchievementsView av => av.Hints,
            SettingsView => "A:Valider|Y:Ouvrir le site|B:Retour",
            _ => "",
        };

        if (_overlays.Count == 0) hints += Dock.IsKeyboardFocusWithin ? "|≡:Menu" : "|⧉:Applications|≡:Menu";

        HintsBox.Visibility = string.IsNullOrEmpty(hints) ? Visibility.Collapsed : Visibility.Visible;
        HintsBar.Children.Clear();
        foreach (var part in hints.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = part.IndexOf(':');
            var key = part[..sep];
            var glyph = new Border { Style = (Style)FindResource("PadGlyph") };
            glyph.Child = new TextBlock
            {
                Text = key,
                Style = (Style)FindResource("PadGlyphText"),
                Foreground = new SolidColorBrush(key switch
                {
                    "A" => Color.FromRgb(0x6B, 0xD3, 0x6B),
                    "B" => Color.FromRgb(0xFF, 0x5C, 0x5C),
                    "X" => Color.FromRgb(0x4D, 0xA3, 0xFF),
                    "Y" => Color.FromRgb(0xFF, 0xD2, 0x4D),
                    _ => Color.FromRgb(0xF3, 0xF5, 0xF9),
                }),
            };
            // Écart entre deux aides, mais pas avant la première (le bord de la barre suffit).
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(HintsBar.Children.Count == 0 ? 0 : 20, 0, 0, 0) };
            item.Children.Add(glyph);
            item.Children.Add(new TextBlock { Text = part[(sep + 1)..], FontSize = 14, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
            HintsBar.Children.Add(item);
        }
        Dispatcher.BeginInvoke(PlaceHints, DispatcherPriority.Loaded);
    }

    /// <summary>Clavier ouvert : la barre d'aides monte en haut de l'écran, centrée ; sinon elle reprend sa place.</summary>
    private void PlaceHints()
    {
        var top = _overlays.LastOrDefault() is KeyboardPanel;
        var x = top ? (HintsBox.ActualWidth - Pages.ActualWidth) / 2 + HintsBox.Margin.Right : 0;
        var y = top ? 14 - (RootGrid.ActualHeight - HintsBox.Margin.Bottom - HintsBox.ActualHeight) : 0;
        var move = (TranslateTransform)HintsBox.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Anim.Ms(300)) { EasingFunction = Anim.Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Anim.Ms(300)) { EasingFunction = Anim.Out });
    }

    private void Toast(string icon, string text)
    {
        ToastIcon.Text = icon;
        ToastText.Text = text;
        Anim.Enter(ToastBox, 0, -16, 300);
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
