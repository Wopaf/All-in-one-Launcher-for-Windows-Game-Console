using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Path = System.Windows.Shapes.Path;

namespace MaRogAlly.Views;

/// <summary>
/// Succès de toutes les plateformes réunis : jeux à gauche (progression), succès du jeu sélectionné à droite.
/// Tout se pilote sans quitter la liste : Y trie, X filtre les jeux, LT / RT choisit les succès affichés.
/// → ou A entre dans les succès du jeu, ← ou B en ressort.
/// </summary>
public partial class AchievementsView : UserControl
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public event Action? HintsChanged;

    /// <summary>Téléchargement (avec cache) de l'icône d'un succès : renvoie un chemin local.</summary>
    public Func<string?, Task<string?>>? IconLoader { get; set; }
    /// <summary>Jaquette d'un jeu qui n'a pas de jeu associé dans la bibliothèque.</summary>
    public Func<AchievementGame, Task<string?>>? CoverLoader { get; set; }

    private static readonly (string Key, string Label)[] GameSorts =
        [("recent", "Récents"), ("count", "Plus de succès"), ("progress", "Progression"), ("alpha", "A → Z")];
    private static readonly (string Key, string Label)[] GameFilters =
        [("all", "Tous les jeux"), ("platinum", "Platine uniquement")];
    private static readonly (string Key, string Label)[] ItemSorts =
        [("recent", "Récents"), ("rare", "Plus rares"), ("common", "Plus courants"), ("alpha", "A → Z")];

    private enum Segment { All, Unlocked, Locked }
    private static readonly string[] SegmentLabels = ["Tous", "Débloqués", "À débloquer"];

    private List<AchievementGame> _games = [], _shown = [];
    private int _sort, _gameFilter, _itemSort;
    private Segment _segment;
    private AchievementGame? _current;
    private bool _loaded;
    private Button? _lastGameRow;
    private readonly SlideHighlight _listHighlight, _itemsHighlight;
    private readonly DispatcherTimer _showTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _compact;

    private static readonly Color Unlocked = Color.FromRgb(0x6B, 0xD3, 0x6B);

    public AchievementsView()
    {
        InitializeComponent();
        _listHighlight = new SlideHighlight(ListHighlight, ListHost);
        _itemsHighlight = new SlideHighlight(ItemsHighlight, ItemsHost);
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); BuildItems(); };
        SizeChanged += (_, _) => ApplyCompact(ActualHeight < 760);
        ItemsScroll.ScrollChanged += (_, _) => LoadVisibleIcons();
        UpdateControls();
        ShowEmpty("Chargement des succès…");
    }

    private void ApplyCompact(bool compact)
    {
        _compact = compact;
        ListColumn.Width = new GridLength(compact ? 330 : 390);
        GapColumn.Width = new GridLength(compact ? 32 : 48);
        ListTitle.FontSize = compact ? 28 : 34;
        TitleText.FontSize = compact ? 28 : 38;
        PercentText.FontSize = compact ? 24 : 30;
        StatCards.Margin = new Thickness(0, compact ? 10 : 14, 0, 0);
        ItemsBar.Margin = new Thickness(0, compact ? 12 : 18, 0, compact ? 6 : 10);
    }

    // ---------------- Données ----------------

    public void SetData(List<AchievementGame> games, bool loaded, bool steamPrivate = false)
    {
        PrivateNote.Visibility = steamPrivate ? Visibility.Visible : Visibility.Collapsed;
        _games = games;
        _loaded = loaded;
        var unlocked = games.Sum(g => g.Unlocked);
        var completed = games.Count(g => g.Completed);
        SummaryText.Text = games.Count == 0 ? ""
            : $"{unlocked.ToString("N0", Fr)} débloqués  ·  {completed} jeu{(completed > 1 ? "x" : "")} à 100 %  ·  {games.Count} jeux";
        ApplyList(keepFocus: List.IsKeyboardFocusWithin);
    }

    /// <summary>Reconstruit la liste des jeux selon le tri et le filtre choisis.</summary>
    private void ApplyList(bool keepFocus)
    {
        var key = _current?.Key;
        var list = GameFilters[_gameFilter].Key == "platinum" ? _games.Where(g => g.Completed) : _games;
        list = GameSorts[_sort].Key switch
        {
            "count" => list.OrderByDescending(g => g.Unlocked).ThenByDescending(g => g.Ratio).ThenBy(g => g.DisplayTitle),
            "progress" => list.OrderByDescending(g => g.Ratio).ThenByDescending(g => g.Unlocked).ThenBy(g => g.DisplayTitle),
            "alpha" => list.OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderByDescending(g => g.LastUnlock ?? DateTime.MinValue).ThenByDescending(g => g.Unlocked).ThenBy(g => g.DisplayTitle),
        };
        _shown = list.ToList();

        List.Children.Clear();
        _listHighlight.Hide();
        _lastGameRow = null;
        ListScroll.ScrollToVerticalOffset(0);
        foreach (var g in _shown) List.Children.Add(BuildGameRow(g));

        if (_shown.Count == 0)
        {
            _current = null;
            ShowEmpty(!_loaded ? "Chargement des succès…"
                : _games.Count == 0 ? "Aucun succès trouvé. Renseigne ta clé Web API Steam dans les Réglages, ou connecte tes comptes (Epic, Battle.net…) à GOG Galaxy pour les retrouver ici."
                : "Aucun jeu platine pour l'instant : débloque tous les succès d'un jeu pour le voir ici. X pour revoir tous les jeux.");
            return;
        }

        var target = _shown.FirstOrDefault(g => g.Key == key) ?? _shown[0];
        var row = List.Children.OfType<Button>().First(b => b.Tag == target);
        if (keepFocus || (IsVisible && Keyboard.FocusedElement is null or Window)) FocusRow(row);
        else
        {
            _current = target;
            ShowHeader(target, animate: false);
            BuildItems();
        }
    }

    private void ShowEmpty(string text)
    {
        Header.Visibility = Visibility.Hidden;
        ItemsBar.Visibility = Visibility.Collapsed;
        Items.Children.Clear();
        _itemsHighlight.Hide();
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    // ---------------- Liste des jeux ----------------

    private Button BuildGameRow(AchievementGame g)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cover = new Grid { Width = 36, Height = 52, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2A)) };
        Rounded.SetRadius(cover, 6);
        // Logo de la plateforme en attendant la jaquette (ou s'il n'y en a pas).
        cover.Children.Add(new Path
        {
            Data = Geometry.Parse(PlatformInfo.Of(g.Platform).Logo), Fill = (Brush)FindResource("TextMuted"), Stretch = Stretch.Uniform,
            Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        var img = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(img, 80);
        // Lié au jeu : la jaquette apparaît aussi si elle se télécharge après coup.
        if (g.Game is { } game) img.SetBinding(ImageLoader.SourceProperty, new Binding(nameof(Game.CoverPath)) { Source = game });
        else LoadCover(img, g);
        cover.Children.Add(img);
        grid.Children.Add(cover);

        // Titre, puis barre de progression avec le logo de la plateforme.
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        text.Children.Add(new TextBlock { Text = g.DisplayTitle, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
        var line = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(new Path
        {
            Data = Geometry.Parse(PlatformInfo.Of(g.Platform).Logo), Fill = (Brush)FindResource("TextMuted"),
            Stretch = Stretch.Uniform, Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = g.Completed ? (Brush)FindResource("Gold") : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * g.Ratio;
        bar.Children.Add(fill);
        Grid.SetColumn(bar, 1);
        line.Children.Add(bar);
        text.Children.Add(line);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Pourcentage (étoile dorée pour un jeu terminé), et le compte en dessous.
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 54 };
        var percent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (g.Completed)
            percent.Children.Add(new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = (Brush)FindResource("Gold"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
        percent.Children.Add(new TextBlock
        {
            Text = $"{g.Ratio:P0}", FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15,
            Foreground = (Brush)FindResource(g.Completed ? "Gold" : "TextPrimary"),
        });
        right.Children.Add(percent);
        right.Children.Add(new TextBlock
        {
            Text = $"{g.Unlocked} / {g.Total}", FontSize = 12, Foreground = (Brush)FindResource("TextMuted"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = new Button { Content = grid, Tag = g, Style = (Style)FindResource("RowButton"), Padding = new Thickness(12, 8, 16, 8) };
        RowOptions.SetFloating(row, true);
        row.GotKeyboardFocus += (_, _) =>
        {
            _lastGameRow = row;
            _listHighlight.MoveTo(row);
            _itemsHighlight.Dim(0);
            Select(g);
            UpdateControls();
            HintsChanged?.Invoke();
        };
        row.Click += (_, _) => EnterItems();
        return row;
    }

    private void FocusRow(Button row) => Dispatcher.BeginInvoke(() => FocusNav.Focus(row), DispatcherPriority.Input);

    private void Select(AchievementGame g)
    {
        var changed = !ReferenceEquals(g, _current);
        _current = g;
        ShowHeader(g, animate: changed);
        if (!changed) return;
        // Liste reconstruite un instant après : défilement rapide de la liste des jeux sans à-coups.
        _showTimer.Stop();
        _showTimer.Start();
    }

    private void ShowHeader(AchievementGame g, bool animate)
    {
        Header.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        PlatinumBadge.Visibility = g.Completed ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = g.DisplayTitle;

        PercentText.Text = $"{g.Ratio:P0}";
        PercentText.Foreground = (Brush)FindResource(g.Completed ? "Gold" : "TextPrimary");
        CountText.Text = $"{g.Unlocked} / {g.Total} succès";
        ProgressBar.UpdateLayout();
        ProgressFill.Background = g.Completed ? (Brush)FindResource("Gold") : new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(ProgressBar.ActualWidth * g.Ratio, Anim.Ms(animate ? 500 : 0)) { EasingFunction = Anim.Out });

        LastValue.Text = g.LastUnlock is { } last ? last.ToString("d MMMM yyyy", Fr) : "—";
        var rare = g.Items.Where(a => a.Unlocked && a.Rarity is not null).MinBy(a => a.Rarity);
        RareValue.Text = rare is not null ? $"{rare.Name} · {FormatRarity(rare.Rarity!.Value)}"
            : g.HasDetails ? "—" : "Non fourni par la plateforme";
        var left = g.Total - g.Unlocked;
        LeftValue.Text = left == 0 ? "Aucun" : $"{left} succès";
        LeftValue.Foreground = (Brush)FindResource(left == 0 ? "Gold" : "TextPrimary");

        if (animate) Anim.Enter(Header, 0, 12, 300);
    }

    private static string FormatRarity(double r) => $"{r.ToString(r < 10 ? "0.#" : "0", Fr)} %";

    /// <summary>Niveau de rareté d'après la part des joueurs qui ont le succès.</summary>
    private (string Label, Brush Color) RarityTier(double r) => r switch
    {
        < 1 => ("Légendaire", (Brush)FindResource("Gold")),
        < 5 => ("Très rare", (Brush)FindResource("Gold")),
        < 15 => ("Rare", new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF))),
        < 40 => ("Peu commun", (Brush)FindResource("TextSecondary")),
        _ => ("Commun", (Brush)FindResource("TextMuted")),
    };

    // ---------------- Succès du jeu ----------------

    private const int ItemsBatch = 24;
    /// <summary>Marge sous la zone visible où les icônes se chargent déjà.</summary>
    private const double IconsAhead = 300;
    private int _itemsBuild;
    private readonly List<(Button Row, Action Load)> _pendingIcons = [];

    private void BuildItems()
    {
        var build = ++_itemsBuild; // une construction en cours pour un autre jeu s'arrête
        _pendingIcons.Clear();
        Items.Children.Clear();
        _itemsHighlight.Hide();
        ItemsScroll.ScrollToVerticalOffset(0);
        if (_current is not { } g) return;

        if (!g.HasDetails)
        {
            ItemsBar.Visibility = Visibility.Collapsed;
            EmptyText.Text = $"{PlatformInfo.Of(g.Platform).Name} ne fournit que le nombre de succès débloqués ({g.Unlocked} sur {g.Total}) : la liste détaillée n'est pas disponible pour ce jeu.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        ItemsBar.Visibility = Visibility.Visible;
        BuildSegments(g);

        var filtered = g.Items.Where(a => _segment switch { Segment.Unlocked => a.Unlocked, Segment.Locked => !a.Unlocked, _ => true });
        var items = (ItemSorts[_itemSort].Key switch
        {
            // Rareté inconnue : en fin de liste dans les deux sens.
            "rare" => filtered.OrderBy(a => a.Rarity ?? double.MaxValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "common" => filtered.OrderByDescending(a => a.Rarity ?? -1).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "alpha" => filtered.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            // Débloqués du plus récent au plus ancien, puis les autres du plus courant au plus rare.
            _ => filtered.OrderByDescending(a => a.Unlocked).ThenByDescending(a => a.UnlockedAt ?? DateTime.MinValue).ThenByDescending(a => a.Rarity ?? -1),
        }).ToList();

        if (items.Count == 0)
        {
            EmptyText.Text = _segment == Segment.Unlocked ? "Aucun succès débloqué pour l'instant." : "Tous les succès sont débloqués, bravo !";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        // Un jeu peut avoir des centaines de succès : les premières lignes tout de suite, la suite par petits lots.
        foreach (var a in items.Take(ItemsBatch)) Items.Children.Add(BuildItemRow(a));
        Anim.Enter(Items, 0, 14, 280);
        Dispatcher.BeginInvoke(LoadVisibleIcons, DispatcherPriority.Loaded);
        if (items.Count > ItemsBatch) AppendItems(items, build);
    }

    private async void AppendItems(List<Achievement> items, int build)
    {
        for (var i = ItemsBatch; i < items.Count; i += ItemsBatch)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (build != _itemsBuild) return;
            foreach (var a in items.Skip(i).Take(ItemsBatch)) Items.Children.Add(BuildItemRow(a));
        }
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (build == _itemsBuild) LoadVisibleIcons();
    }

    /// <summary>Lance le chargement des icônes qui arrivent à l'écran (les autres attendent le défilement).</summary>
    private void LoadVisibleIcons()
    {
        for (var i = _pendingIcons.Count - 1; i >= 0; i--)
        {
            var (row, load) = _pendingIcons[i];
            if (row.ActualHeight <= 0 || row.TranslatePoint(new Point(0, 0), ItemsScroll).Y > ItemsScroll.ViewportHeight + IconsAhead) continue;
            _pendingIcons.RemoveAt(i);
            load();
        }
    }

    /// <summary>Choix des succès affichés, avec leur nombre : le segment actif est plein.</summary>
    private void BuildSegments(AchievementGame g)
    {
        Segments.Children.Clear();
        var counts = new[] { g.Items.Count, g.Items.Count(a => a.Unlocked), g.Items.Count(a => !a.Unlocked) };
        for (var i = 0; i < SegmentLabels.Length; i++)
        {
            var index = i;
            var active = (int)_segment == i;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = SegmentLabels[i], FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = active ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x11)) : (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = counts[i].ToString(), FontSize = 12, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? new SolidColorBrush(Color.FromArgb(0x99, 0x0A, 0x0C, 0x11)) : (Brush)FindResource("TextMuted"),
            });
            var seg = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 5, 13, 5), Cursor = Cursors.Hand, Child = content,
                Background = active ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF9)) : Brushes.Transparent,
            };
            seg.MouseLeftButtonUp += (_, _) => SetSegment((Segment)index);
            Segments.Children.Add(seg);
        }
    }

    private Button BuildItemRow(Achievement a)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_compact ? 58 : 68) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_compact ? 130 : 150) });

        // Icône ; verrouillé : atténuée, avec un petit cadenas.
        var size = _compact ? 44 : 52;
        var frame = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2A)) };
        Rounded.SetRadius(frame, 8);
        // Trophée en attendant l'icône (ou si Steam n'en a pas) : jamais de case vide.
        frame.Children.Add(new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, Foreground = (Brush)FindResource("TextMuted"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        var icon = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(icon, 96);
        // L'atténuation est portée par un parent : le fondu d'apparition remet l'opacité de l'image à 1.
        frame.Children.Add(new Grid { Opacity = a.Unlocked ? 1 : 0.4, Children = { icon } });
        if (!a.Unlocked)
            frame.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0C, 0x11)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 2),
                Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, Foreground = (Brush)FindResource("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        grid.Children.Add(frame);

        var secret = a.Hidden && !a.Unlocked;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        text.Children.Add(new TextBlock
        {
            Text = a.Name, FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = _compact ? 15 : 17,
            Foreground = (Brush)FindResource(a.Unlocked ? "TextPrimary" : "TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var desc = secret ? "Succès secret : débloque-le pour découvrir comment l'obtenir." : a.Description;
        if (desc.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = desc, FontSize = 13, Foreground = (Brush)FindResource(a.Unlocked ? "TextSecondary" : "TextMuted"),
                FontStyle = secret ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(0, 2, 0, 0),
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Statut : date d'obtention (coche verte), puis rareté (niveau, barre, part des joueurs).
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (a.Unlocked)
        {
            var date = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            date.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = new SolidColorBrush(Unlocked), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            date.Children.Add(new TextBlock { Text = a.UnlockedAt?.ToString("d MMM yyyy", Fr) ?? "Débloqué", FontSize = 13, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(date);
        }
        if (a.Rarity is { } r)
        {
            var (tier, color) = RarityTier(r);
            right.Children.Add(new TextBlock
            {
                Text = $"{tier} · {FormatRarity(r)}", FontSize = 12, Foreground = color, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, a.Unlocked ? 4 : 0, 0, 0),
            });
            var bar = new Grid { Width = 90, Height = 3, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = color, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, 90 * Math.Clamp(r, 0, 100) / 100) });
            right.Children.Add(bar);
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = new Button { Content = grid, Style = (Style)FindResource("RowButton"), Padding = new Thickness(14, 10, 18, 10) };
        _pendingIcons.Add((row, () => LoadIcon(icon, a.Unlocked ? a.Icon : a.IconLocked ?? a.Icon, a.Icon)));
        RowOptions.SetFloating(row, true);
        row.GotKeyboardFocus += (_, _) =>
        {
            _itemsHighlight.MoveTo(row);
            _listHighlight.Dim(0.5);
            UpdateControls();
            HintsChanged?.Invoke();
        };
        return row;
    }

    private async void LoadIcon(Image image, string? url, string? fallback)
    {
        if (IconLoader is null || url is null) return;
        var path = await IconLoader(url);
        if (path is null && fallback != url) path = await IconLoader(fallback);
        if (path is not null) ImageLoader.SetSource(image, path);
    }

    private async void LoadCover(Image image, AchievementGame g)
    {
        if (CoverLoader is not null && await CoverLoader(g) is { } path) ImageLoader.SetSource(image, path);
    }

    // ---------------- Tri, filtre, affichage ----------------

    /// <summary>Libellés des commandes ; celles de la zone qui n'a pas la main sont atténuées.</summary>
    private void UpdateControls()
    {
        GameSortText.Text = $"Tri : {GameSorts[_sort].Label}";
        GameFilterText.Text = GameFilters[_gameFilter].Label;
        ItemSortText.Text = $"Tri : {ItemSorts[_itemSort].Label}";
        var inItems = InItems;
        GameControls.Opacity = inItems ? 0.45 : 1;
        ItemSortPill.Opacity = inItems ? 1 : 0.45;
    }

    private void CycleGameSort()
    {
        _sort = (_sort + 1) % GameSorts.Length;
        UpdateControls();
        ApplyList(keepFocus: true);
    }

    private void CycleGameFilter()
    {
        _gameFilter = (_gameFilter + 1) % GameFilters.Length;
        UpdateControls();
        ApplyList(keepFocus: true);
    }

    private void CycleItemSort()
    {
        _itemSort = (_itemSort + 1) % ItemSorts.Length;
        UpdateControls();
        RebuildItems();
    }

    private void SetSegment(Segment segment)
    {
        if (segment == _segment) return;
        _segment = segment;
        RebuildItems();
    }

    /// <summary>Reconstruit les succès en gardant la main dans la liste si on y était.</summary>
    private void RebuildItems()
    {
        var wasInItems = InItems;
        _showTimer.Stop();
        BuildItems();
        if (wasInItems)
        {
            if (Items.Children.Count > 0) EnterItems();
            else BackToList();
        }
        HintsChanged?.Invoke();
    }

    private void GameSort_Click(object sender, MouseButtonEventArgs e) => CycleGameSort();
    private void GameFilter_Click(object sender, MouseButtonEventArgs e) => CycleGameFilter();
    private void ItemSort_Click(object sender, MouseButtonEventArgs e) => CycleItemSort();

    /// <summary>Paramètres de confidentialité du profil Steam (dans le client Steam s'il est installé).</summary>
    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://steamcommunity.com/my/edit/settings";
        using var steam = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("steam");
        GameLauncher.Open(steam is not null ? $"steam://openurl/{url}" : url);
    }

    // ---------------- Navigation ----------------

    private bool InItems => Items.IsKeyboardFocusWithin;
    private bool OnPrivacy => PrivacyButton.IsKeyboardFocused;

    private void EnterItems()
    {
        if (_showTimer.IsEnabled) { _showTimer.Stop(); BuildItems(); }
        if (Items.Children.OfType<Button>().FirstOrDefault() is { } first)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(first), DispatcherPriority.Loaded);
    }

    private void BackToList()
    {
        _itemsHighlight.Hide();
        if (_lastGameRow is { IsVisible: true } row) FocusNav.Focus(row);
        else if (List.Children.OfType<Button>().FirstOrDefault() is { } first) FocusNav.Focus(first);
        UpdateControls();
    }

    public string Hints => InItems
        ? "↕:Défiler|LT/RT:Afficher|Y:Trier|B:Jeux"
        : OnPrivacy ? "A:Ouvrir|↓:Jeux"
        : "A:Voir les succès|Y:Trier les jeux|X:Filtrer|LT/RT:Afficher";

    /// <summary>Sélectionne un jeu (depuis sa fiche) ; le filtre est retiré s'il le cachait.</summary>
    public void ShowGame(AchievementGame game)
    {
        if (_shown.All(g => g.Key != game.Key))
        {
            _gameFilter = 0;
            UpdateControls();
            ApplyList(keepFocus: false);
        }
        if (List.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as AchievementGame)?.Key == game.Key) is not { } row) return;
        _lastGameRow = row;
        FocusRow(row);
    }

    public void FocusFirst()
    {
        if (List.IsKeyboardFocusWithin || Items.IsKeyboardFocusWithin) return;
        var target = _lastGameRow is { IsVisible: true } last ? last
            : List.Children.OfType<Button>().FirstOrDefault(b => b.Tag == _current) ?? List.Children.OfType<Button>().FirstOrDefault();
        if (target is not null) FocusRow(target);
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Down or NavAction.Back when OnPrivacy:
                BackToList();
                HintsChanged?.Invoke();
                return true;
            case NavAction.Accept when OnPrivacy:
                FocusNav.Activate();
                return true;
            case NavAction.Up or NavAction.Down:
                if (InItems) FocusNav.Move(action, Items);
                // Du premier jeu, haut mène au bouton de confidentialité Steam s'il est affiché.
                else if (!FocusNav.Move(action, List) && action == NavAction.Up && PrivateNote.Visibility == Visibility.Visible)
                {
                    FocusNav.Focus(PrivacyButton);
                    _listHighlight.Dim(0.5);
                    HintsChanged?.Invoke();
                }
                return true;
            case NavAction.Right or NavAction.Accept when !InItems:
                EnterItems();
                return true;
            case NavAction.Left or NavAction.Back when InItems:
                BackToList();
                HintsChanged?.Invoke();
                return true;
            case NavAction.Secondary:
                if (InItems) CycleItemSort();
                else CycleGameSort();
                return true;
            case NavAction.Option:
                if (!InItems) CycleGameFilter();
                return true;
            case NavAction.PrevLetter:
                SetSegment((Segment)(((int)_segment + SegmentLabels.Length - 1) % SegmentLabels.Length));
                return true;
            case NavAction.NextLetter:
                SetSegment((Segment)(((int)_segment + 1) % SegmentLabels.Length));
                return true;
            // Depuis la liste des jeux, gauche va vers la barre de gauche.
            case NavAction.Right or NavAction.Accept:
                return true;
        }
        return false;
    }
}
