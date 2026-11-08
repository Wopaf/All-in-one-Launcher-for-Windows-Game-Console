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
/// Onglet Découverte : une barre de recherche (bibliothèque, Steam et GOG à la fois), les tuiles des listes, puis
/// les nouveautés (remplacées par les résultats pendant une recherche) à côté des jeux offerts par Epic.
/// La navigation se fait par zones : recherche, tuiles, jaquettes, offres Epic.
/// </summary>
public partial class ExploreView : UserControl
{
    public static readonly DependencyProperty CoverWidthProperty = DependencyProperty.Register(nameof(CoverWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(130.0));
    public static readonly DependencyProperty CoverHeightProperty = DependencyProperty.Register(nameof(CoverHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(195.0));
    public static readonly DependencyProperty FreeWidthProperty = DependencyProperty.Register(nameof(FreeWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(240.0));
    public static readonly DependencyProperty FreeHeightProperty = DependencyProperty.Register(nameof(FreeHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(110.0));
    public static readonly DependencyProperty FreeCardWidthProperty = DependencyProperty.Register(nameof(FreeCardWidth), typeof(double), typeof(ExploreView), new PropertyMetadata(240.0));
    public static readonly DependencyProperty FreeScrollHeightProperty = DependencyProperty.Register(nameof(FreeScrollHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(300.0));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(ExploreView), new PropertyMetadata(90.0));
    public static readonly DependencyProperty TileTitleSizeProperty = DependencyProperty.Register(nameof(TileTitleSize), typeof(double), typeof(ExploreView), new PropertyMetadata(19.0));

    public double CoverWidth { get => (double)GetValue(CoverWidthProperty); set => SetValue(CoverWidthProperty, value); }
    public double CoverHeight { get => (double)GetValue(CoverHeightProperty); set => SetValue(CoverHeightProperty, value); }
    public double FreeWidth { get => (double)GetValue(FreeWidthProperty); set => SetValue(FreeWidthProperty, value); }
    public double FreeHeight { get => (double)GetValue(FreeHeightProperty); set => SetValue(FreeHeightProperty, value); }
    public double FreeCardWidth { get => (double)GetValue(FreeCardWidthProperty); set => SetValue(FreeCardWidthProperty, value); }
    public double FreeScrollHeight { get => (double)GetValue(FreeScrollHeightProperty); set => SetValue(FreeScrollHeightProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public double TileTitleSize { get => (double)GetValue(TileTitleSizeProperty); set => SetValue(TileTitleSizeProperty, value); }

    public static Geometry SteamLogo { get; } = Logo(Platform.Steam);
    public static Geometry GogLogo { get; } = Logo(Platform.Gog);
    private static Geometry Logo(Platform p)
    {
        var g = Geometry.Parse(PlatformInfo.Of(p).Logo);
        g.Freeze();
        return g;
    }

    /// <summary>Nombre de jeux affichés dans la section Nouveautés (la liste complète reste sur son bouton).</summary>
    private const int NewGamesShown = 8;
    private const string SearchPlaceholder = "Rechercher un jeu sur Steam et GOG…";

    /// <summary>A sur la barre : ouvrir le clavier.</summary>
    public event Action? SearchRequested;
    public event Action<SearchHit>? OpenHit;
    /// <summary>Jeu de la section Nouveautés.</summary>
    public event Action<DiscoverGame>? OpenNew;
    /// <summary>Page d'un jeu offert dans le magasin Epic (confirmée par un écran dédié).</summary>
    public event Action<FreeGame>? OpenFree;
    /// <summary>Tuile : ouvre la page des tendances sur cette liste (ou sur la liste de souhaits).</summary>
    public event Action<string>? OpenTrends;
    /// <summary>Y sur un jeu de la section Nouveautés, ou sur un résultat.</summary>
    public event Action<DiscoverGame>? WishNewGame;
    public event Action<SearchHit>? WishHit;
    public event Action<string?>? HeroRequested;

    private readonly List<DiscoverTile> _tiles =
    [
        new(DiscoverView.WishlistKey, "Ma liste de souhaits", "Les jeux que tu veux garder à l'œil", Color.FromRgb(0xFF, 0x5C, 0x8A)),
        new("soon", "Prochainement", "Les sorties les plus attendues", Color.FromRgb(0xA6, 0x5C, 0xFF)),
        new("played", "Les plus joués", "En ce moment sur Steam", Color.FromRgb(0xFF, 0x7A, 0x2E)),
        new("new", "Nouveautés", "Sorties récentes populaires", Color.FromRgb(0x2D, 0x8C, 0xFF)),
        new("sellers", "Meilleures ventes", "Les plus achetés sur Steam", Color.FromRgb(0x22, 0xB0, 0x63)),
    ];
    private List<DiscoverGame> _newGames = [];
    private List<FreeGame> _free = [];
    private int _freeColumns = 1;
    private string _query = "";

    /// <summary>Dit si un jeu est dans la liste de souhaits (pour les cœurs sur les jaquettes).</summary>
    public Func<string, bool>? IsWished { get; set; }
    private object? _selected;
    private Button? _lastFocused;
    private Button? _lastCover;

    public ExploreView()
    {
        InitializeComponent();
        Tiles.ItemsSource = _tiles;
        QueryText.Text = SearchPlaceholder;
        SizeChanged += (_, _) => Relayout();
        // Le défilement est animé par FocusNav : sans ça, WPF saute d'abord jusqu'à la jaquette et l'animation repart de l'ancienne position.
        foreach (var list in new[] { Hits, NewGames, FreeGames }) list.RequestBringIntoView += (_, e) => e.Handled = true;
        ShowNewSection();
    }

    public string Query => _query;

    /// <summary>Rangée réservée sous la liste (voir <see cref="PlaceInfo"/>).</summary>
    private const double InfoRowHeight = 34;

    private void Relayout()
    {
        if (ActualHeight <= 0) return;
        var compact = ActualHeight < 760;
        var gap = compact ? 14 : 24;
        SearchButton.Height = compact ? 50 : 60;
        TileHeight = compact ? 88 : 116;
        TileTitleSize = compact ? 17 : 20;
        Tiles.Margin = new Thickness(64, gap, 46, 0);
        MainArea.Margin = new Thickness(0, gap, 0, 0);
        FreeWidth = Math.Clamp(Math.Floor(ActualWidth * 0.25), 260, 460);
        // Un peu plus de quatre jaquettes visibles (la cinquième, coupée, annonce la suite), sans dépasser la hauteur restante.
        var coversWidth = ActualWidth - 2 * 64 - FreeWidth - 36;
        var fromWidth = ((coversWidth + 18) / 4.4 - 18) * 1.5;
        var fromHeight = ActualHeight - SearchButton.Height - TileHeight - 2 * gap - 44 - InfoRowHeight;
        CoverHeight = Math.Clamp(Math.Floor(Math.Min(fromWidth, fromHeight)), 110, 420);
        CoverWidth = Math.Floor(CoverHeight / 1.5);
        LayoutFree();
        // Hauteur stable : le bloc centré ne saute pas entre les nouveautés, une recherche en cours et les résultats.
        ResultsArea.MinHeight = 30 + CoverHeight + InfoRowHeight;
    }

    /// <summary>Jeux offerts sur la hauteur des jaquettes : empilés, en deux colonnes à partir de quatre (trois rangées visibles, les suivantes défilent).</summary>
    private void LayoutFree()
    {
        _freeColumns = _free.Count >= 4 ? 2 : 1;
        var rows = Math.Clamp((_free.Count + _freeColumns - 1) / _freeColumns, 1, 3);
        FreeCardWidth = Math.Floor((FreeWidth - 12 * (_freeColumns - 1)) / _freeColumns);
        FreeHeight = Math.Floor((CoverHeight - 12 * (rows - 1)) / rows);
        FreeScrollHeight = CoverHeight + 52;
    }

    private void UpdateFreeSection() => FreeSection.Visibility = _free.Count > 0 && _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- Données ----------------

    /// <summary>Tendances Steam : les nouveautés sont affichées telles quelles, le n° 1 des autres listes sert de visuel à leur tuile.</summary>
    public void SetTrends(IReadOnlyList<DiscoverSection> sections)
    {
        foreach (var t in _tiles.Where(t => t.Key != DiscoverView.WishlistKey)) t.Section = sections.FirstOrDefault(s => s.Key == t.Key);
        var hadFocus = NewGames.IsKeyboardFocusWithin;
        if (_selected is DiscoverGame) Select(null);
        _newGames = sections.FirstOrDefault(s => s.Key == "new")?.Games.Take(NewGamesShown).ToList() ?? [];
        RefreshWishMarks();
        NewGames.ItemsSource = _newGames;
        if (_query.Length == 0) ShowNewSection();
        if (hadFocus && _newGames.Count > 0) FocusIn(NewGames, 0);
    }

    /// <summary>Le premier jeu de la liste de souhaits sert de visuel à sa tuile.</summary>
    public void SetWishlist(List<DiscoverGame> games)
        => _tiles.First(t => t.Key == DiscoverView.WishlistKey).Section = new DiscoverSection { Games = games };

    /// <summary>Jeux offerts par Epic (actuels et à venir).</summary>
    public void SetFreeGames(List<FreeGame> games)
    {
        var hadFocus = FreeGames.IsKeyboardFocusWithin;
        if (_selected is FreeGame) Select(null);
        _free = games;
        FreeGames.ItemsSource = games;
        LayoutFree();
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
        foreach (var game in _newGames) game.IsWished = IsWished(game.WishKey);
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
        ResultsLabel.Text = "NOUVEAUTÉS";
        ResultsCount.Text = _newGames.Count > 0 ? "Sorties récentes populaires sur Steam" : "";
        if (_newGames.Count == 0)
        {
            ShowMessage("Chargement des nouveautés…", "Les sorties récentes populaires sur Steam arrivent.");
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
        InfoLine.Margin = searching ? new Thickness(64, 12, 64, 0) : new Thickness(24, 0, 0, 14);
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
        DiscoverTile t => t.HeroPath,
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

    private void Tile_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _lastFocused = sender as Button;
        if ((sender as FrameworkElement)?.DataContext is DiscoverTile t) Select(t);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiscoverTile t) return;
        OpenTrends?.Invoke(t.Key);
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
        : _selected is DiscoverGame ? "A:Ouvrir|Y:Souhait|X:Rechercher"
        : "A:Ouvrir|X:Rechercher";

    public event Action? HintsChanged;

    public void FocusFirst()
    {
        if (IsKeyboardFocusWithin) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_lastFocused is { IsVisible: true } last && last.IsDescendantOf(this)) FocusNav.Focus(last);
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

    /// <summary>Sous une tuile : les offres Epic si elle est au-dessus d'elles, sinon la jaquette visible la plus proche.</summary>
    private Button? BelowTile(Button tile, List<Button> covers, List<Button> free)
    {
        if (free.Count == 0) return covers.Count > 0 ? Nearest(covers, tile) : null;
        var edge = FreeSection.TranslatePoint(new Point(0, 0), this).X;
        var visible = covers.Where(c => CenterX(c) < edge).ToList();
        return CenterX(tile) >= edge || visible.Count == 0 ? free[0] : Nearest(visible, tile);
    }

    /// <summary>Vrai si la sélection a bougé (faux au bord : gauche va alors vers la barre de gauche).</summary>
    private bool MoveFocus(NavAction action)
    {
        var current = Keyboard.FocusedElement as Button;
        List<Button> search = ResetButton.IsEnabled ? [SearchButton, ResetButton] : [SearchButton];
        var tiles = Buttons(Tiles);
        var covers = HitsScroll.Visibility == Visibility.Visible ? Buttons(Hits) : NewScroll.Visibility == Visibility.Visible ? Buttons(NewGames) : [];
        var free = FreeSection.Visibility == Visibility.Visible ? Buttons(FreeGames) : [];
        Button? target;
        if (current is not null && search.Contains(current))
            target = action switch
            {
                NavAction.Left => Step(search, current, -1),
                NavAction.Right => Step(search, current, 1),
                NavAction.Down => tiles.FirstOrDefault(),
                _ => null,
            };
        else if (current is not null && tiles.Contains(current))
            target = action switch
            {
                NavAction.Left => Step(tiles, current, -1),
                NavAction.Right => Step(tiles, current, 1),
                NavAction.Up => SearchButton,
                NavAction.Down => BelowTile(current, covers, free),
                _ => null,
            };
        else if (current is not null && covers.Contains(current))
            target = action switch
            {
                NavAction.Left => Step(covers, current, -1),
                NavAction.Right => Step(covers, current, 1) ?? free.FirstOrDefault(),
                NavAction.Up => Nearest(tiles, current),
                NavAction.Down => free.FirstOrDefault(),
                _ => null,
            };
        else if (current is not null && free.Contains(current))
            target = action switch
            {
                NavAction.Left when free.IndexOf(current) % _freeColumns > 0 => Step(free, current, -1),
                NavAction.Left => covers.Contains(_lastCover!) ? _lastCover : covers.FirstOrDefault(),
                NavAction.Right when free.IndexOf(current) % _freeColumns < _freeColumns - 1 => Step(free, current, 1),
                NavAction.Up => Step(free, current, -_freeColumns) ?? Nearest(tiles, current),
                NavAction.Down => Step(free, current, _freeColumns),
                _ => null,
            };
        else target = SearchButton;
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
                else if (_selected is DiscoverGame game && game.Owned is null) WishNewGame?.Invoke(game);
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

/// <summary>Tuile de l'onglet Découverte menant à une liste des tendances Steam.</summary>
public sealed class DiscoverTile(string key, string title, string subtitle, Color tint) : ObservableObject
{
    private DiscoverSection? _section;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public Brush Tint { get; } = Freeze(new SolidColorBrush(tint));

    public DiscoverSection? Section
    {
        get => _section;
        set { if (Set(ref _section, value)) OnPropertyChanged(nameof(Lead)); }
    }

    /// <summary>N° 1 de la liste : son visuel sert de fond.</summary>
    public DiscoverGame? Lead => _section?.Games.FirstOrDefault();
    public string? HeroPath => Lead is { } g ? g.HeroPath ?? g.CoverPath : null;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
