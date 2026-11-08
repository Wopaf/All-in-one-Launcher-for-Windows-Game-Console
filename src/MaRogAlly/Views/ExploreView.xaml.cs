using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Onglet Découverte, une page qui défile : « À la une » (les jeux du moment, l'un après l'autre), une barre de recherche
/// (bibliothèque, Steam et GOG à la fois), la rangée En tendance (remplacée par les résultats pendant une recherche),
/// une rangée par liste (liste de souhaits, les plus attendus, les plus joués, en promotion), puis les jeux offerts par Epic. La navigation se fait par zones, de haut en bas.
/// </summary>
public partial class ExploreView : UserControl
{
    public static readonly DependencyProperty CoverWidthProperty = DependencyProperty.Register(nameof(CoverWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(130.0));
    public static readonly DependencyProperty CoverHeightProperty = DependencyProperty.Register(nameof(CoverHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(195.0));
    public static readonly DependencyProperty FreeWidthProperty = DependencyProperty.Register(nameof(FreeWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(240.0));
    public static readonly DependencyProperty FreeHeightProperty = DependencyProperty.Register(nameof(FreeHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(110.0));
    public static readonly DependencyProperty FreeCardWidthProperty = DependencyProperty.Register(nameof(FreeCardWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(240.0));
    public static readonly DependencyProperty FreeScrollHeightProperty = DependencyProperty.Register(nameof(FreeScrollHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(300.0));

    public double CoverWidth { get => (double)GetValue(CoverWidthProperty); set => SetValue(CoverWidthProperty, value); }
    public double CoverHeight { get => (double)GetValue(CoverHeightProperty); set => SetValue(CoverHeightProperty, value); }
    public double FreeWidth { get => (double)GetValue(FreeWidthProperty); set => SetValue(FreeWidthProperty, value); }
    public double FreeHeight { get => (double)GetValue(FreeHeightProperty); set => SetValue(FreeHeightProperty, value); }
    public double FreeCardWidth { get => (double)GetValue(FreeCardWidthProperty); set => SetValue(FreeCardWidthProperty, value); }
    public double FreeScrollHeight { get => (double)GetValue(FreeScrollHeightProperty); set => SetValue(FreeScrollHeightProperty, value); }

    public static Geometry SteamLogo { get; } = Logo(Platform.Steam);
    public static Geometry GogLogo { get; } = Logo(Platform.Gog);
    private static Geometry Logo(Platform p)
    {
        var g = Geometry.Parse(PlatformInfo.Of(p).Logo);
        g.Freeze();
        return g;
    }

    /// <summary>Nombre de jeux affichés dans la rangée En tendance (la liste complète reste sur sa tuile).</summary>
    private const int NewGamesShown = 12;
    /// <summary>Jeux par rangée des autres listes.</summary>
    private const int RowGamesShown = 12;
    private List<TrendRow> _rows = [];
    private const string SearchPlaceholder = "Rechercher un jeu sur Steam et GOG…";

    /// <summary>A sur la barre : ouvrir le clavier.</summary>
    public event Action? SearchRequested;
    public event Action<SearchHit>? OpenHit;
    /// <summary>Jeu de la rangée En tendance, ou d'À la une.</summary>
    public event Action<DiscoverGame>? OpenNew;
    /// <summary>Page d'un jeu offert dans le magasin Epic (confirmée par un écran dédié).</summary>
    public event Action<FreeGame>? OpenFree;
    /// <summary>Y sur un jeu d'une rangée (vendu sur Steam, ou déjà dans la liste de souhaits), ou sur un résultat.</summary>
    public event Action<DiscoverGame>? WishNewGame;
    public event Action<SearchHit>? WishHit;
    public event Action<string?>? HeroRequested;

    private IReadOnlyList<DiscoverSection> _sections = [];
    private List<DiscoverGame> _wishGames = [];
    private List<DiscoverGame> _newGames = [];
    private string _trendingSubtitle = "";
    /// <summary>À la une : les jeux qui défilent dans le grand bandeau.</summary>
    private List<DiscoverGame> _featured = [];
    private int _featuredIndex;
    private readonly DispatcherTimer _spotlightTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private List<FreeGame> _free = [];
    private string _query = "";

    /// <summary>Dit si un jeu est dans la liste de souhaits (pour les cœurs sur les jaquettes).</summary>
    public Func<string, bool>? IsWished { get; set; }
    private object? _selected;
    private Button? _lastFocused;
    private Button? _lastCover;

    public ExploreView()
    {
        InitializeComponent();
        QueryText.Text = SearchPlaceholder;
        SizeChanged += (_, _) => Relayout();
        // Le défilement est animé par FocusNav : sans ça, WPF saute d'abord jusqu'à la jaquette et l'animation repart de l'ancienne position.
        foreach (var list in new[] { Hits, NewGames, FreeGames }) list.RequestBringIntoView += (_, e) => e.Handled = true;
        // La page défile d'elle-même vers la sélection (sans le saut de WPF).
        PageContent.RequestBringIntoView += (_, e) => e.Handled = true;
        // Fondu en haut et en bas de la page, seulement du côté où il reste du contenu caché.
        PageScroll.ScrollChanged += (_, _) => UpdateScrollFade();
        PageScroll.SizeChanged += (_, _) => UpdateScrollFade();
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is FrameworkElement f) Dispatcher.BeginInvoke(() => ScrollPage(f), DispatcherPriority.Loaded);
        };
        ShowNewSection();
        // Le bandeau défile tout seul tant qu'il est à l'écran et que la sélection n'y est pas (←→ le font défiler).
        _spotlightTimer.Tick += (_, _) => ShowFeatured(_featuredIndex + 1, 1);
        IsVisibleChanged += (_, _) => UpdateSpotlightTimer();
    }

    // ---------------- À la une ----------------

    private void UpdateSpotlightTimer()
    {
        if (IsVisible && Spotlight.Visibility == Visibility.Visible && _featured.Count > 1 && !Spotlight.IsKeyboardFocused) _spotlightTimer.Start();
        else _spotlightTimer.Stop();
    }

    /// <summary>Jeu suivant (direction 1) ou précédent (-1) : le contenu s'efface en glissant, le nouveau arrive de l'autre côté.</summary>
    private void ShowFeatured(int index, int direction)
    {
        if (_featured.Count == 0) return;
        index = (index % _featured.Count + _featured.Count) % _featured.Count;
        var game = _featured[index];
        _featuredIndex = index;
        UpdateDots();
        if (direction == 0 || SpotlightBody.DataContext is null)
        {
            SpotlightBody.DataContext = game;
        }
        else
        {
            Anim.Exit(SpotlightBody, -30 * direction, 0, 160, () =>
            {
                SpotlightBody.DataContext = game;
                Anim.Enter(SpotlightBody, 40 * direction, 0, 420);
            });
        }
        if (Spotlight.IsKeyboardFocused) Select(game);
        // Minuterie relancée : un jeu choisi à la main reste affiché sept secondes.
        if (_spotlightTimer.IsEnabled) { _spotlightTimer.Stop(); _spotlightTimer.Start(); }
    }

    private void UpdateDots()
    {
        SpotlightDots.Children.Clear();
        if (_featured.Count < 2) return;
        for (var i = 0; i < _featured.Count; i++)
        {
            var active = i == _featuredIndex;
            SpotlightDots.Children.Add(new Border
            {
                Width = active ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 0, 0),
                Background = active ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            });
        }
    }

    private void Spotlight_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = Spotlight;
        UpdateSpotlightTimer();
        if (SpotlightBody.DataContext is DiscoverGame g) Select(g);
    }

    private void Spotlight_Blur(object sender, KeyboardFocusChangedEventArgs e) => UpdateSpotlightTimer();

    private void Spotlight_Click(object sender, RoutedEventArgs e)
    {
        if (SpotlightBody.DataContext is DiscoverGame g) OpenNew?.Invoke(g);
    }

    public string Query => _query;

    /// <summary>Rangée réservée sous la liste (voir <see cref="PlaceInfo"/>).</summary>
    private const double InfoRowHeight = 34;

    private void Relayout()
    {
        if (ActualHeight <= 0) return;
        var compact = ActualHeight < 760;
        var gap = compact ? 16 : 24;
        SearchButton.Height = compact ? 50 : 60;
        // La page défile : chaque bloc garde une taille confortable au lieu de se tasser pour tenir dans l'écran.
        var spot = Spotlight.Visibility == Visibility.Visible;
        Spotlight.Height = compact ? 290 : Math.Clamp(Math.Floor(ActualHeight * 0.44), 320, 560);
        SpotlightTitle.FontSize = compact ? 34 : 46;
        SpotlightText.Margin = compact ? new Thickness(30, 0, 30, 0) : new Thickness(44, 0, 44, 0);
        Spotlight.Margin = new Thickness(64, gap, 64, 0);
        FreeSection.Margin = new Thickness(0, gap + 10, 0, 0);
        MainArea.Margin = new Thickness(0, spot || FreeSection.Visibility == Visibility.Visible ? gap + 10 : gap, 0, 0);
        // Un peu plus de cinq jaquettes visibles par rangée (la suivante, coupée, annonce la suite).
        CoverWidth = Math.Clamp(Math.Floor((ActualWidth - 128 + 18) / 5.5 - 18), 120, 260);
        CoverHeight = Math.Floor(CoverWidth * 1.5);
        FreeCardWidth = compact ? 320 : 420;
        FreeHeight = Math.Floor(FreeCardWidth * 0.46);
        // Hauteur stable : la rangée ne saute pas entre les tendances, une recherche en cours et les résultats.
        ResultsArea.MinHeight = 30 + CoverHeight + InfoRowHeight;
    }

    /// <summary>
    /// La page suit la sélection : tout en haut pour à la une et la recherche ; sinon la rangée choisie au milieu
    /// de l'écran, avec son titre.
    /// </summary>
    private void ScrollPage(FrameworkElement f)
    {
        if (!f.IsVisible || !f.IsDescendantOf(PageContent) || PageScroll.ScrollableHeight <= 0) return;
        double target;
        if (f == Spotlight || f == SearchButton || f == ResetButton) target = 0;
        else
        {
            var y = f.TranslatePoint(new Point(0, 0), PageContent).Y;
            target = y - (PageScroll.ViewportHeight - f.ActualHeight) / 2 - 16;
        }
        target = Math.Clamp(target, 0, PageScroll.ScrollableHeight);
        if (Math.Abs(target - PageScroll.VerticalOffset) > 1) Anim.ScrollTo(PageScroll, target);
    }

    private const double FadeHeight = 56;

    /// <summary>
    /// Masque en dégradé : les éléments coupés par le bord s'effacent au lieu d'être tranchés net.
    /// Chaque bord ne s'estompe qu'à mesure qu'il reste du contenu de ce côté (rien en haut de page).
    /// </summary>
    private void UpdateScrollFade()
    {
        var h = PageScroll.ActualHeight;
        if (h <= 0) return;
        var top = Math.Clamp(PageScroll.VerticalOffset / FadeHeight, 0, 1);
        var bottom = Math.Clamp((PageScroll.ScrollableHeight - PageScroll.VerticalOffset) / FadeHeight, 0, 1);
        if (top == 0 && bottom == 0)
        {
            PageScroll.OpacityMask = null;
            return;
        }
        var f = Math.Min(FadeHeight / h, 0.3);
        static Color A(double hidden) => Color.FromArgb((byte)Math.Round(255 * (1 - hidden)), 0, 0, 0);
        // En coordonnées absolues : en relatif, WPF cale le dégradé sur tout le contenu (la page entière), pas sur la partie visible.
        PageScroll.OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(A(top), 0),
            new(Colors.Black, f),
            new(Colors.Black, 1 - f),
            new(A(bottom), 1),
        }, new Point(0, 0), new Point(0, h)) { MappingMode = BrushMappingMode.Absolute };
    }

    private void Row_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    /// <summary>Pendant une recherche, seuls les résultats restent sous la barre (sans à la une, offres ni listes).</summary>
    private void UpdateFreeSection()
    {
        var idle = _query.Length == 0;
        FreeSection.Visibility = _free.Count > 0 && idle ? Visibility.Visible : Visibility.Collapsed;
        MoreRows.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        var spot = _featured.Count > 0 && idle ? Visibility.Visible : Visibility.Collapsed;
        if (Spotlight.Visibility != spot)
        {
            Spotlight.Visibility = spot;
            UpdateSpotlightTimer();
        }
        Relayout();
    }

    // ---------------- Données ----------------

    /// <summary>Tendances : à la une dans le bandeau, la rangée En tendance, puis une rangée par liste.</summary>
    public void SetTrends(IReadOnlyList<DiscoverSection> sections)
    {
        _sections = sections;
        var hadFocus = NewGames.IsKeyboardFocusWithin;
        if (_selected is DiscoverGame) Select(null);

        var spotWasVisible = Spotlight.Visibility == Visibility.Visible;
        _featured = sections.FirstOrDefault(s => s.Key == DiscoverService.FeaturedKey)?.Games ?? [];
        Spotlight.Visibility = _featured.Count > 0 && _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (spotWasVisible && _featured.Count == 0 && Spotlight.IsKeyboardFocused) FocusNav.Focus(SearchButton);
        SpotlightBody.DataContext = null;
        ShowFeatured(0, 0);
        UpdateSpotlightTimer();
        if (spotWasVisible != (_featured.Count > 0)) Relayout();

        var trending = sections.FirstOrDefault(s => s.Key == "trending");
        _trendingSubtitle = trending?.Subtitle ?? "";
        _newGames = trending?.Games.Take(NewGamesShown).ToList() ?? [];

        BuildRows();
        NewGames.ItemsSource = _newGames;
        if (_query.Length == 0) ShowNewSection();
        if (hadFocus && _newGames.Count > 0) FocusIn(NewGames, 0);
    }

    /// <summary>Liste de souhaits : sa rangée, juste sous En tendance (absente tant qu'elle est vide).</summary>
    public void SetWishlist(List<DiscoverGame> games)
    {
        _wishGames = games;
        BuildRows();
    }

    /// <summary>Rangées sous En tendance ; la sélection garde sa place (même rangée, même jaquette) si elle y était.</summary>
    private void BuildRows()
    {
        var lists = RowLists();
        var current = Keyboard.FocusedElement as Button;
        var row = lists.FindIndex(r => current is not null && r.Contains(current));
        var column = row >= 0 ? lists[row].IndexOf(current!) : -1;

        var rows = new List<TrendRow>();
        if (_wishGames.Count > 0)
            rows.Add(new TrendRow("MA LISTE DE SOUHAITS", "Les jeux que tu veux garder à l'œil", _wishGames.Take(RowGamesShown).ToList()));
        rows.AddRange(new[] { "anticipated", "played", "deals" }
            .Select(key => _sections.FirstOrDefault(s => s.Key == key)).OfType<DiscoverSection>().Where(s => s.Games.Count > 0)
            .Select(s => new TrendRow(s.Title.ToUpperInvariant(), s.Subtitle, s.Games.Take(RowGamesShown).ToList())));
        _rows = rows;
        MoreRows.ItemsSource = _rows;
        RefreshWishMarks();
        if (row < 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            MoreRows.UpdateLayout();
            var fresh = RowLists().Where(r => r.Count > 0).ToList();
            if (fresh.Count == 0)
            {
                FocusNav.Focus(SearchButton);
                return;
            }
            var target = fresh[Math.Min(row, fresh.Count - 1)];
            FocusNav.Focus(target[Math.Clamp(column, 0, target.Count - 1)]);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Jeux offerts par Epic (actuels et à venir).</summary>
    public void SetFreeGames(List<FreeGame> games)
    {
        var hadFocus = FreeGames.IsKeyboardFocusWithin;
        if (_selected is FreeGame) Select(null);
        _free = games;
        FreeGames.ItemsSource = games;
        // Places vides : toujours au moins 4 cartes dans la rangée.
        FreeShadows.ItemsSource = Enumerable.Range(0, Math.Max(0, 4 - games.Count)).ToList();
        UpdateFreeSection();
        if (hadFocus && games.Count > 0) FocusIn(FreeGames, 0);
    }

    public void RefreshCountdowns()
    {
        foreach (var g in _free) g.RefreshCountdown();
    }

    /// <summary>Recherche lancée : on vide les résultats et on annonce la recherche (sans texte : retour aux nouveautés).</summary>
    public void BeginSearch(string query)
    {
        _query = query.Trim();
        QueryText.Text = _query.Length > 0 ? _query : SearchPlaceholder;
        QueryText.Foreground = (Brush)FindResource(_query.Length > 0 ? "TextPrimary" : "TextMuted");
        ResetButton.IsEnabled = _query.Length > 0;
        RefreshWishMarks();
        UpdateFreeSection();
        Hits.ItemsSource = null;
        if (_query.Length == 0)
        {
            ShowNewSection();
            return;
        }
        PlaceInfo(searching: true);
        ResultsLabel.Text = $"RÉSULTATS POUR « {_query.ToUpperInvariant()} »";
        ResultsCount.Text = "";
        ShowMessage("Recherche en cours…", $"Bibliothèque, Steam et GOG pour « {_query} ».");
    }

    /// <summary>Résultats (éventuellement partiels : la bibliothèque arrive avant les boutiques).</summary>
    public void SetResults(string query, List<SearchHit> hits, bool done)
    {
        if (query != _query) return; // réponse d'une recherche remplacée depuis
        var focusedIndex = Hits.ItemsSource is List<SearchHit> old && _selected is SearchHit sel ? old.IndexOf(sel) : -1;
        var hadFocus = Hits.IsKeyboardFocusWithin;
        Hits.ItemsSource = hits;
        RefreshWishMarks();
        HitsScroll.ScrollToHorizontalOffset(0);
        var counts = hits.GroupBy(h => h.Owned is not null ? "Bibliothèque" : h.Platform.Name).Select(g => $"{g.Key} {g.Count()}");
        ResultsCount.Text = hits.Count == 0 ? "" : string.Join("  ·  ", counts);
        if (hits.Count == 0)
        {
            if (done) ShowMessage("Aucun jeu trouvé", "Vérifie l'orthographe, ou essaie avec moins de mots.");
            else ShowMessage("Recherche en cours…", $"Bibliothèque, Steam et GOG pour « {_query} ».");
            return;
        }
        EmptyState.Visibility = NewScroll.Visibility = Visibility.Collapsed;
        HitsScroll.Visibility = Visibility.Visible;
        ShowHit(hits[Math.Clamp(focusedIndex, 0, hits.Count - 1)]);
        if (hadFocus) FocusIn(Hits, Math.Max(0, focusedIndex));
    }

    /// <summary>Remet à jour les cœurs des jaquettes affichées.</summary>
    public void RefreshWishMarks()
    {
        if (IsWished is null) return;
        foreach (var game in _newGames.Concat(_rows.SelectMany(r => r.Games))) game.IsWished = IsWished(game.WishKey);
        if (Hits.ItemsSource is List<SearchHit> hits)
            foreach (var hit in hits) hit.IsWished = hit.WishKey is { } key && IsWished(key);
    }

    /// <summary>Après la recherche : la sélection passe sur le premier résultat.</summary>
    public void FocusResults()
    {
        if (Hits.Items.Count > 0) FocusIn(Hits, 0);
        else Dispatcher.BeginInvoke(() => FocusNav.Focus(SearchButton), DispatcherPriority.Input);
    }

    /// <summary>Amène la rangée affichée à une position (0 à 1), pour vérifier l'affichage (« --row »).</summary>
    public void PreviewRow(double position)
    {
        var row = HitsScroll.Visibility == Visibility.Visible ? HitsScroll : NewScroll;
        row.ScrollToHorizontalOffset(row.ScrollableWidth * position);
    }

    /// <summary>Sans recherche : la section Nouveautés occupe le milieu de l'écran.</summary>
    private void ShowNewSection()
    {
        PlaceInfo(searching: false);
        ResultsLabel.Text = "EN TENDANCE CETTE SEMAINE";
        ResultsCount.Text = _newGames.Count > 0 ? _trendingSubtitle : "";
        if (_newGames.Count == 0)
        {
            ShowMessage("Chargement des tendances…", "Les jeux du moment arrivent.");
            return;
        }
        EmptyState.Visibility = HitsScroll.Visibility = Visibility.Collapsed;
        NewScroll.Visibility = Visibility.Visible;
    }

    /// <summary>Jeu sélectionné et décompte : en bas à gauche de la liste pendant une recherche, sinon à droite du titre.</summary>
    private void PlaceInfo(bool searching)
    {
        Grid.SetRow(InfoLine, searching ? 2 : 0);
        Grid.SetColumn(InfoLine, searching ? 0 : 1);
        Grid.SetColumnSpan(InfoLine, searching ? 2 : 1);
        InfoLine.Margin = searching ? new Thickness(64, 12, 64, 0) : new Thickness(24, 0, 64, 14);
        ResultsCount.HorizontalAlignment = HitInfo.HorizontalAlignment = searching ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void ShowMessage(string title, string hint)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
        HitsScroll.Visibility = NewScroll.Visibility = Visibility.Collapsed;
    }

    // ---------------- Sélection ----------------

    /// <summary>
    /// Le jeu sélectionné (nom, prix, sortie, avis) n'est plus écrit sur la ligne d'information : sa jaquette et sa fiche
    /// suffisent. La ligne garde le décompte des résultats, ou le titre de la rangée.
    /// </summary>
    private void SetInfo(string title, string detail)
    {
        HitTitle.Text = "";
        HitDetail.Text = "";
        HitInfo.Visibility = Visibility.Collapsed;
        ResultsCount.Visibility = Visibility.Visible;
    }

    private void ShowHit(SearchHit h)
    {
        SetInfo(h.Title, h.Info);
        Select(h);
    }

    private void ShowNew(DiscoverGame d)
    {
        var state = d.Owned is { } owned ? (owned.IsInstalled ? "Installé" : "Dans ta bibliothèque")
            : d.PriceText is { } price ? (d.DiscountText is { } off ? $"{price}  ({off})" : price) : null;
        SetInfo(d.Name, string.Join(" · ", new[] { state, d.ReleaseText, d.ReviewText }.Where(s => !string.IsNullOrEmpty(s))));
        Select(d);
    }

    private void Select(object? item)
    {
        _selected = item;
        if (item is not (SearchHit or DiscoverGame)) SetInfo("", "");
        HeroRequested?.Invoke(CurrentHero);
    }

    /// <summary>Visuel de fond de l'élément sélectionné.</summary>
    public string? CurrentHero => _selected switch
    {
        SearchHit h => h.HeroPath ?? h.Owned?.HeroPath ?? h.Steam?.HeroPath ?? h.CoverPath,
        DiscoverGame d => d.HeroPath ?? d.CoverPath,
        FreeGame f => f.ImagePath,
        _ => null,
    };

    private void Hit_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = sender as Button;
        _lastCover = _lastFocused;
        if ((sender as FrameworkElement)?.DataContext is SearchHit h) ShowHit(h);
    }

    private void Hit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchHit h) OpenHit?.Invoke(h);
    }

    private void New_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = sender as Button;
        _lastCover = _lastFocused;
        if ((sender as FrameworkElement)?.DataContext is DiscoverGame d) ShowNew(d);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoverGame d) OpenNew?.Invoke(d);
    }

    private void Free_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = sender as Button;
        if ((sender as FrameworkElement)?.DataContext is FreeGame g) Select(g);
    }

    private void Free_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FreeGame g) OpenFree?.Invoke(g);
    }


    private void Search_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = SearchButton;
        Select(null);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke();

    private void Reset_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = ResetButton;
        Select(null);
    }

    /// <summary>Efface la recherche : retour aux nouveautés, la sélection revient sur la barre.</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        FocusNav.Focus(SearchButton);
        BeginSearch("");
        HintsChanged?.Invoke();
    }

    // ---------------- Navigation ----------------

    public string Hints => Keyboard.FocusedElement == ResetButton ? "A:Effacer la recherche"
        : Keyboard.FocusedElement == SearchButton
        ? (_query.Length > 0 ? "A:Modifier la recherche|X:Effacer" : "A:Rechercher")
        : _selected is SearchHit { WishKey: not null } ? "A:Ouvrir|Y:Souhait|X:Nouvelle recherche"
        : _selected is SearchHit ? "A:Ouvrir|X:Nouvelle recherche"
        : Keyboard.FocusedElement == Spotlight && _featured.Count > 1
            ? (_selected is DiscoverGame { HasSteam: true } ? "A:Ouvrir|←→:Défiler|Y:Souhait" : "A:Ouvrir|←→:Défiler")
        : _selected is DiscoverGame { IsWished: true } ? "A:Ouvrir|Y:Retirer|X:Rechercher"
        : _selected is DiscoverGame { HasSteam: true } ? "A:Ouvrir|Y:Souhait|X:Rechercher"
        : "A:Ouvrir|X:Rechercher";

    public event Action? HintsChanged;

    public void FocusFirst()
    {
        if (IsKeyboardFocusWithin) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_lastFocused is { IsVisible: true } last && last.IsDescendantOf(this)) FocusNav.Focus(last);
            else if (Spotlight.IsVisible) FocusNav.Focus(Spotlight);
            else FocusNav.Focus(SearchButton);
            HintsChanged?.Invoke();
        }, DispatcherPriority.Input);
    }

    private void FocusIn(ItemsControl list, int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject c && FindButton(c) is { } b) FocusNav.Focus(b);
            HintsChanged?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    private static Button? FindButton(DependencyObject o)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var child = VisualTreeHelper.GetChild(o, i);
            if (child is Button b) return b;
            if (FindButton(child) is { } found) return found;
        }
        return null;
    }

    private static List<Button> Buttons(ItemsControl list)
        => Enumerable.Range(0, list.Items.Count)
            .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c ? FindButton(c) : null)
            .OfType<Button>().ToList();

    private static Button? Step(List<Button> row, Button from, int direction)
        => row.ElementAtOrDefault(row.IndexOf(from) + direction);

    private double CenterX(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), this).X;

    private Button Nearest(List<Button> row, Button from) => row.MinBy(b => Math.Abs(CenterX(b) - CenterX(from)))!;

    /// <summary>Jaquettes de chaque rangée des autres listes.</summary>
    private List<List<Button>> RowLists() => Enumerable.Range(0, MoreRows.Items.Count)
        .Select(i => MoreRows.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c && FindChild<ItemsControl>(c) is { } list ? Buttons(list) : [])
        .ToList();

    private static T? FindChild<T>(DependencyObject o) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var child = VisualTreeHelper.GetChild(o, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Zones de la page, de haut en bas : recherche, à la une, offres Epic, jaquettes (ou résultats), autres rangées.</summary>
    private List<List<Button>> Zones()
    {
        var zones = new List<List<Button>>();
        zones.Add(ResetButton.IsEnabled ? [SearchButton, ResetButton] : [SearchButton]);
        if (Spotlight.IsVisible) zones.Add([Spotlight]);
        if (FreeSection.IsVisible) zones.Add(Buttons(FreeGames));
        zones.Add(HitsScroll.Visibility == Visibility.Visible ? Buttons(Hits) : NewScroll.Visibility == Visibility.Visible ? Buttons(NewGames) : []);
        if (MoreRows.IsVisible) zones.AddRange(RowLists());
        zones.RemoveAll(z => z.Count == 0);
        return zones;
    }

    /// <summary>Arrivée dans une zone : la barre de recherche (pas le bouton d'effacement) ; depuis un élément pleine largeur, le premier ; sinon le plus proche à l'horizontale.</summary>
    private Button Enter(List<Button> zone, Button from)
        => zone.Contains(SearchButton) ? SearchButton
            : zone.Count == 1 || from.ActualWidth > ActualWidth * 0.5 ? zone[0]
            : Nearest(zone, from);

    /// <summary>Vrai si la sélection a bougé (faux au bord : gauche va alors vers la barre de gauche).</summary>
    private bool MoveFocus(NavAction action)
    {
        var current = Keyboard.FocusedElement as Button;
        if (current is not null && current == Spotlight)
            switch (action)
            {
                // Au premier jeu, gauche va vers la barre de gauche.
                case NavAction.Left when _featuredIndex > 0:
                    ShowFeatured(_featuredIndex - 1, -1);
                    HintsChanged?.Invoke();
                    return true;
                case NavAction.Right when _featured.Count > 1:
                    ShowFeatured(_featuredIndex + 1, 1);
                    HintsChanged?.Invoke();
                    return true;
            }
        var zones = Zones();
        var z = zones.FindIndex(zone => current is not null && zone.Contains(current));
        Button? target;
        if (z < 0) target = SearchButton;
        else
        {
            var zone = zones[z];
            target = action switch
            {
                NavAction.Left => current == Spotlight ? null : Step(zone, current!, -1),
                NavAction.Right => current == Spotlight ? null : Step(zone, current!, 1),
                NavAction.Up => z > 0 ? Enter(zones[z - 1], current!) : null,
                _ => z < zones.Count - 1 ? Enter(zones[z + 1], current!) : null,
            };
        }
        if (target is null) return false;
        FocusNav.Focus(target);
        HintsChanged?.Invoke();
        return true;
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                return MoveFocus(action) || action != NavAction.Left;
            case NavAction.Accept:
                FocusNav.Activate();
                return true;
            case NavAction.Secondary:
                if (_selected is SearchHit hit) WishHit?.Invoke(hit);
                // Ajout : un jeu vendu sur Steam ; retrait : tout jeu déjà dans la liste (même d'une autre boutique).
                else if (_selected is DiscoverGame game && game.Owned is null && (game.HasSteam || game.IsWished)) WishNewGame?.Invoke(game);
                return true;
            case NavAction.Option:
                // Sur la barre avec une recherche : X l'efface ; ailleurs, X relance une recherche.
                if ((Keyboard.FocusedElement == SearchButton || Keyboard.FocusedElement == ResetButton) && _query.Length > 0)
                    Reset_Click(this, new RoutedEventArgs());
                else SearchRequested?.Invoke();
                return true;
        }
        return false;
    }
}

/// <summary>Rangée d'une liste sous la rangée En tendance.</summary>
public sealed record TrendRow(string Label, string Subtitle, List<DiscoverGame> Games);
