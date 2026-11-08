using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Tendances par catégorie (en tendance, les plus attendus, les plus joués, en promotion) et liste de souhaits, ouvertes depuis l'onglet Découverte.</summary>
public partial class DiscoverView : UserControl
{
    public static readonly DependencyProperty CoverWidthProperty = DependencyProperty.Register(nameof(CoverWidth), typeof(double), typeof(DiscoverView), new PropertyMetadata(140.0));
    public static readonly DependencyProperty CoverHeightProperty = DependencyProperty.Register(nameof(CoverHeight), typeof(double), typeof(DiscoverView), new PropertyMetadata(210.0));
    public double CoverWidth { get => (double)GetValue(CoverWidthProperty); set => SetValue(CoverWidthProperty, value); }
    public double CoverHeight { get => (double)GetValue(CoverHeightProperty); set => SetValue(CoverHeightProperty, value); }

    /// <summary>Visuel à afficher en fond pour le jeu sélectionné.</summary>
    public event Action<string?>? HeroRequested;
    /// <summary>Jeu déjà possédé : ouverture de sa fiche.</summary>
    public event Action<Game>? OpenGame;
    /// <summary>Jeu non possédé : sa fiche (détails, captures, liens vers les magasins).</summary>
    public event Action<DiscoverGame>? OpenDetails;
    /// <summary>Y : page du magasin Steam directement.</summary>
    public event Action<DiscoverGame>? OpenStore;
    /// <summary>Y dans la liste de souhaits : retire le jeu.</summary>
    public event Action<DiscoverGame>? RemoveWish;
    public event Action? HintsChanged;

    public const string WishlistKey = "wishlist";
    /// <summary>Toujours présente, avant les tendances, même sans connexion.</summary>
    private readonly DiscoverSection _wish = new() { Key = WishlistKey, Title = "Ma liste de souhaits", Subtitle = "Les jeux que tu veux garder à l'œil" };
    private bool _loaded;
    private List<DiscoverSection> _sections = [];
    /// <summary>À la une : affiché par l'onglet Découverte, pas ici ; ses jeux sont tout de même reliés à la bibliothèque.</summary>
    private DiscoverSection? _featured;
    private readonly Dictionary<string, int> _lastIndex = [];
    private int _section = -1;
    /// <summary>Catégorie demandée depuis l'onglet Découverte, à retrouver quand les données arrivent.</summary>
    private string? _pendingKey;
    private DiscoverGame? _selected;
    private DateTime _lastChange;
    private IReadOnlyList<Game> _library = [];

    public DiscoverView()
    {
        InitializeComponent();
        // Le défilement est animé par FocusNav : sans ça, WPF saute d'abord jusqu'à la jaquette et l'animation repart de l'ancienne position.
        Covers.RequestBringIntoView += (_, e) => e.Handled = true;
        _sections = [_wish];
        BuildTabs();
        SizeChanged += (_, _) => Relayout();
    }

    private void Relayout()
    {
        if (ActualHeight <= 0) return;
        var compact = ActualHeight < 760;
        CoverHeight = Math.Clamp(Math.Floor(ActualHeight * (compact ? 0.3 : 0.32)), 120, 340);
        CoverWidth = Math.Floor(CoverHeight / 1.5);
        CoversHost.MinHeight = CoverHeight; // les onglets restent en place sur une liste vide
        TitleText.FontSize = compact ? 32 : 46;
        TitleText.LineHeight = compact ? 36 : 52;
        TitleText.MaxHeight = TitleText.LineHeight * 2;
        DescText.MaxHeight = compact ? 48 : 72;
        DetailPanel.Margin = new Thickness(64, 0, 64, compact ? 22 : 32);
    }

    // ---------------- Données ----------------

    public void SetSections(List<DiscoverSection> sections, bool loaded)
    {
        var key = _pendingKey ?? _sections.ElementAtOrDefault(_section)?.Key;
        _loaded = loaded;
        _featured = sections.FirstOrDefault(s => s.Key == DiscoverService.FeaturedKey);
        _sections = [_wish, .. sections.Where(s => s != _featured)];
        MatchOwned(_library);
        BuildTabs();
        var keep = _sections.FindIndex(s => s.Key == key);
        if (keep >= 0) _pendingKey = null;
        else if (sections.Count > 0) keep = 1;
        // Les données arrivent souvent après l'affichage : on sélectionne si rien d'autre n'a le focus (ex. une fiche ouverte).
        var focus = IsVisible && (IsKeyboardFocusWithin || Keyboard.FocusedElement is null or Window);
        ShowSection(keep, 0, focus);
    }

    /// <summary>Remplace les jeux de la liste de souhaits ; la sélection garde sa place si la liste est affichée.</summary>
    public void SetWishlist(List<DiscoverGame> games)
    {
        var shown = ReferenceEquals(_sections.ElementAtOrDefault(_section), _wish);
        if (_selected is { } sel && _wish.Games.IndexOf(sel) is var index and >= 0) _lastIndex[WishlistKey] = index;
        var focus = shown && IsVisible && (IsKeyboardFocusWithin || Keyboard.FocusedElement is null or Window);
        if (shown) _selected = null;
        _wish.Games = games;
        MatchOwned(_library);
        if (shown) ShowSection(_section, 0, focus);
    }

    /// <summary>Jeux des tendances (la liste de souhaits a ses propres mises à jour).</summary>
    public IEnumerable<DiscoverGame> AllGames => _sections.Where(s => s != _wish).SelectMany(s => s.Games);

    /// <summary>Repère les jeux déjà présents dans la bibliothèque (identifiant Steam, sinon titre).</summary>
    public void MatchOwned(IReadOnlyList<Game> library)
    {
        _library = library;
        var bySteam = new Dictionary<int, Game>();
        foreach (var g in library)
        {
            var id = g.SteamAppId ?? (g.Platform == Platform.Steam && int.TryParse(g.PlatformId, out var pid) ? pid : null);
            if (id is int i && (!bySteam.TryGetValue(i, out var prev) || g.IsInstalled && !prev.IsInstalled)) bySteam[i] = g;
        }
        var byTitle = library.GroupBy(g => Text.Normalize(g.Title))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(g => g.IsInstalled).First());
        foreach (var d in _sections.Append(_featured).OfType<DiscoverSection>().SelectMany(s => s.Games))
            d.Owned = bySteam.GetValueOrDefault(d.AppId) ?? byTitle.GetValueOrDefault(Text.Normalize(d.Name));
        if (_selected is { } s) Show(s, animate: false);
    }

    // ---------------- Catégories ----------------

    private void BuildTabs()
    {
        SectionTabs.Children.Clear();
        for (var i = 0; i < _sections.Count; i++)
        {
            var index = i;
            var tab = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(16, 9, 16, 9),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = _sections[i].Title,
                    FontFamily = (FontFamily)FindResource("Display"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            tab.MouseLeftButtonUp += (_, _) => ShowSection(index, Math.Sign(index - _section), focus: true);
            SectionTabs.Children.Add(tab);
        }
    }

    private void UpdateTabs()
    {
        for (var i = 0; i < SectionTabs.Children.Count; i++)
        {
            var tab = (Border)SectionTabs.Children[i];
            var active = i == _section;
            tab.Background = active ? new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            tab.BorderBrush = active ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            tab.BorderThickness = new Thickness(1);
            ((TextBlock)tab.Child).Foreground = (Brush)FindResource(active ? "TextPrimary" : "TextMuted");
        }
    }

    /// <summary>Ouverture depuis l'onglet Découverte : affiche la catégorie demandée (sinon la première).</summary>
    public void ShowSection(string key)
    {
        var index = _sections.FindIndex(s => s.Key == key);
        _pendingKey = index < 0 ? key : null;
        ShowSection(index, 0, focus: true);
    }

    private void ShowSection(int index, int direction, bool focus)
    {
        // On mémorise la position dans la catégorie quittée.
        if (_selected is { } sel && _sections.ElementAtOrDefault(_section) is { } old && old.Games.IndexOf(sel) is var oi and >= 0)
            _lastIndex[old.Key] = oi;

        _section = index;
        var section = _sections.ElementAtOrDefault(index);
        var games = section?.Games ?? [];
        UpdateTabs();
        SectionSubtitle.Text = section?.Subtitle ?? "";
        DetailPanel.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        Covers.ItemsSource = games;
        RowScroller.ScrollToHorizontalOffset(0);
        if (direction != 0) Anim.Enter(Covers, 0, 24 * direction, 300);
        EmptyText.Text = section == _wish ? "Ta liste de souhaits est vide. Appuie sur Y sur une jaquette de l'onglet Découverte, ou utilise le bouton de la fiche d'un jeu."
            : _loaded ? "Impossible de récupérer les tendances pour le moment." : "Chargement des tendances…";
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (games.Count == 0)
        {
            _selected = null;
            HeroRequested?.Invoke(null);
            HintsChanged?.Invoke();
            return;
        }

        var target = Math.Clamp(_lastIndex.GetValueOrDefault(section!.Key), 0, games.Count - 1);
        Show(games[target], animate: true);
        HintsChanged?.Invoke();
        if (focus) FocusAt(target);
    }

    private void FocusAt(int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Covers.UpdateLayout();
            if (Covers.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject c && FindButton(c) is { } b) FocusNav.Focus(b);
            else FocusNav.FocusFirst(Covers);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
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

    // ---------------- Fiche ----------------

    private void Show(DiscoverGame g, bool animate)
    {
        var changed = !ReferenceEquals(g, _selected);
        _selected = g;
        var section = _sections.ElementAtOrDefault(_section);
        KickerLogo.Data = Geometry.Parse(PlatformInfo.Of(g.StorePlatform).Logo);
        // Logo du magasin seulement pour un jeu qui y est vendu (pas pour une exclusivité console).
        KickerLogo.Visibility = g.HasSteam || g.OtherStoreUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        KickerText.Text = (g.Rank is int r ? $"{section?.Title} · N° {r}" : section?.Title ?? "").ToUpperInvariant();
        TitleText.Text = g.Name;
        DescText.Text = g.Description;

        Badges.Children.Clear();
        if (g.Owned is { } owned)
            AddBadge(owned.IsInstalled ? "Installé" : "Dans ta bibliothèque", "", Color.FromRgb(0x6B, 0xD3, 0x6B), (Brush)FindResource("FreeGreen"));
        if (g.PlatformsText is { } platforms) AddBadge(platforms, "\uE7FC", null);
        if (g.PlayersText is { } players) AddBadge(players, "", null);
        if (g.PriceText is { } price) AddBadge(g.DiscountText is { } d ? $"{price}  {d}" : price, null, null,
            g.DiscountText is not null || g.IsFree ? (Brush)FindResource("FreeGreen") : null);
        if (g.ReviewText is { } reviews) AddBadge(reviews, "", Color.FromRgb(0xFF, 0xC8, 0x3D));
        if (g.ReleaseText is { } release) AddBadge(release, "", null);

        if (changed && animate && DateTime.UtcNow - _lastChange > TimeSpan.FromMilliseconds(140))
            Anim.Enter(DetailPanel, 0, 14, 320);
        _lastChange = DateTime.UtcNow;
        HeroRequested?.Invoke(g.HeroPath ?? g.CoverPath);
    }

    private void AddBadge(string text, string? icon, Color? iconColor, Brush? background = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
            panel.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 12,
                Foreground = iconColor is { } c ? new SolidColorBrush(c) : (Brush)FindResource("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)FindResource("Display"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var badge = new Border { Style = (Style)FindResource("InfoBadge"), Child = panel };
        if (background is not null)
        {
            badge.Background = background;
            badge.BorderThickness = new Thickness(0);
            if (icon is not null) ((TextBlock)panel.Children[0]).Foreground = Brushes.White;
        }
        Badges.Children.Add(badge);
    }

    /// <summary>Visuel de fond du jeu sélectionné.</summary>
    public string? CurrentHero => _selected is { } g ? g.HeroPath ?? g.CoverPath : null;

    /// <summary>Les visuels arrivent après l'affichage : on rafraîchit le fond si besoin.</summary>
    public void ImagesLoaded()
    {
        if (_selected is { } g && IsVisible) HeroRequested?.Invoke(g.HeroPath ?? g.CoverPath);
    }

    private void Card_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiscoverGame g) return;
        Show(g, animate: true);
        HintsChanged?.Invoke();
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiscoverGame g) return;
        if (g.Owned is { } owned) OpenGame?.Invoke(owned);
        else OpenDetails?.Invoke(g);
    }

    // ---------------- Navigation ----------------

    public void FocusFirst()
    {
        if (Covers.IsKeyboardFocusWithin) return;
        var section = _sections.ElementAtOrDefault(_section);
        var index = _selected is { } s && section is not null ? Math.Max(0, section.Games.IndexOf(s)) : 0;
        FocusAt(index);
    }

    public string Hints => !ReferenceEquals(_sections.ElementAtOrDefault(_section), _wish)
            ? (_selected is { HasSteam: true } ? "A:Ouvrir|Y:Page Steam|↑↓:Catégorie|B:Retour" : "A:Ouvrir|↑↓:Catégorie|B:Retour")
        : _selected is null ? "↑↓:Catégorie|B:Retour"
        : "A:Ouvrir|Y:Retirer|↑↓:Catégorie|B:Retour";

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Left or NavAction.Right:
                return FocusNav.Move(action, Covers) || action == NavAction.Right;
            case NavAction.Up:
                if (_section > 0) ShowSection(_section - 1, -1, focus: true);
                return true;
            case NavAction.Down:
                if (_section < _sections.Count - 1) ShowSection(_section + 1, 1, focus: true);
                return true;
            case NavAction.Accept:
                FocusNav.Activate();
                return true;
            case NavAction.Secondary:
                if (_selected is not { } g) return true;
                if (ReferenceEquals(_sections.ElementAtOrDefault(_section), _wish)) RemoveWish?.Invoke(g);
                else if (g.HasSteam) OpenStore?.Invoke(g);
                return true;
        }
        return false;
    }
}
