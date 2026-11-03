using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Onglet de jeux : fiche du jeu sélectionné en haut, grille navigable à la manette en bas.</summary>
public partial class GamesView : UserControl
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(GamesView), new PropertyMetadata(150.0));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(GamesView), new PropertyMetadata(225.0));
    public static readonly DependencyProperty TileMarginProperty = DependencyProperty.Register(nameof(TileMargin), typeof(Thickness), typeof(GamesView));
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(GamesView), new PropertyMetadata(260.0));
    public static readonly DependencyProperty RowPaddingProperty = DependencyProperty.Register(nameof(RowPadding), typeof(Thickness), typeof(GamesView));

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public Thickness TileMargin { get => (Thickness)GetValue(TileMarginProperty); set => SetValue(TileMarginProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public Thickness RowPadding { get => (Thickness)GetValue(RowPaddingProperty); set => SetValue(RowPaddingProperty, value); }

    /// <summary>Onglet « Installés » : seuls les jeux installés sont proposés.</summary>
    public bool InstalledOnly { get; set; }
    public GameFilter Filter { get; } = new();

    public Game? Selected => _index >= 0 && _index < _visible.Count ? _visible[_index] : null;
    public IReadOnlyList<Game> Source => _source;

    public event Action<Game?>? SelectionChanged;
    public event Action<Game>? OpenRequested;
    public event Action? FilterRequested, SortRequested;
    /// <summary>Bouton + : choix des catégories affichées.</summary>
    public event Action? CategoriesRequested;

    private List<Game> _source = [];
    private List<Game> _visible = [];
    private int _index = -1;
    private int _cols = 7;
    private ScrollViewer? _scroller;
    private DateTime _lastSelectionChange;
    private SlideHighlight? _categoryHighlight, _categoryPill;
    /// <summary>La sélection est sur la ligne de catégories (et non sur les jeux).</summary>
    private bool _inBar;
    private Button? _lastChip;

    private const double SidePadding = 64;
    private const double Gap = 18;

    public GamesView()
    {
        InitializeComponent();
        GridHost.SizeChanged += (_, _) => Relayout();
        SizeChanged += (_, _) => Relayout();
        RowsList.Loaded += (_, _) => _scroller ??= RowsList.Template.FindName("Scroller", RowsList) as ScrollViewer;
        // Première ouverture de l'onglet : les jaquettes apparaissent à ce moment-là.
        IsVisibleChanged += (_, _) => RevealTiles();
        // Molette : rangée précédente / suivante (une seule rangée est visible).
        RowsList.PreviewMouseWheel += (_, e) =>
        {
            Handle(e.Delta > 0 ? NavAction.Up : NavAction.Down);
            e.Handled = true;
        };
    }

    // ---------------- Données ----------------

    public void SetGames(IEnumerable<Game> all)
    {
        _source = InstalledOnly ? all.Where(g => g.IsInstalled).ToList() : all.ToList();
        Refresh();
    }

    /// <summary>Réapplique filtres et tri en conservant la sélection si possible, ou en repartant du premier jeu.</summary>
    public void Refresh(bool toTop = false)
    {
        var keepId = toTop ? null : Selected?.Id;
        if (Selected is { } old) old.IsSelected = false;

        _visible = Filter.Apply(_source);
        BuildRows();
        _firstRow = 0;

        var idx = keepId is null ? -1 : _visible.FindIndex(g => g.Id == keepId);
        _index = -1;
        Select(idx >= 0 ? idx : 0, animate: false, force: true);

        UpdateCount();
        UpdateEmptyState();
        UpdateCategoryBar();
        ReplaySelection();
        // toTop : filtre, tri ou catégorie changés. Une mise à jour en arrière-plan (scan, fiches) ne rejoue pas l'apparition.
        if (toTop) _revealPending = true;
        RevealTiles();
    }

    // ---------------- Apparition des jaquettes ----------------

    /// <summary>L'apparition reste à jouer : au premier chargement, puis à chaque changement de filtre, de tri ou de catégorie.</summary>
    private bool _revealPending = true;
    private bool _revealAllowed;

    /// <summary>L'écran de lancement s'efface : l'apparition peut se jouer, elle ne serait plus cachée derrière.</summary>
    public void AllowReveal()
    {
        _revealAllowed = true;
        RevealTiles();
    }

    /// <summary>Les jaquettes visibles apparaissent une à une ; les rangées suivantes, hors champ, sont déjà en place.</summary>
    private void RevealTiles()
    {
        if (!_revealPending || !_revealAllowed || !_active || !IsVisible || _visible.Count == 0) return;
        // Rangées générées tout de suite : les jaquettes sont masquées avant d'avoir été affichées une seule fois.
        RowsList.UpdateLayout();
        var tiles = new List<UIElement>();
        for (var r = _firstRow; r < _firstRow + VisibleRows; r++)
        {
            if (RowsList.ItemContainerGenerator.ContainerFromIndex(r) is not DependencyObject row
                || VisualTreeHelper.GetChildrenCount(row) == 0 || VisualTreeHelper.GetChild(row, 0) is not ItemsControl tilesOfRow) continue;
            for (var c = 0; c < tilesOfRow.Items.Count; c++)
                if (tilesOfRow.ItemContainerGenerator.ContainerFromIndex(c) is UIElement tile) tiles.Add(tile);
        }
        if (tiles.Count == 0) return;
        _revealPending = false;
        // Durée totale bornée : sur un très grand écran, davantage de jaquettes, plus rapprochées.
        var step = Math.Min(45, 800.0 / tiles.Count);
        for (var i = 0; i < tiles.Count; i++) Anim.Enter(tiles[i], 0, 18, 320, delay: i * step);
    }

    /// <summary>
    /// Une vignette créée alors que son jeu est déjà sélectionné ne joue pas l'animation de sélection (zoom, halo) :
    /// une fois les rangées construites, on la redéclenche.
    /// </summary>
    private void ReplaySelection() => Dispatcher.BeginInvoke(() =>
    {
        if (!_active || _inBar || _suspended || Selected is not { } g) return;
        g.IsSelected = false;
        g.IsSelected = true;
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>Tri, filtres, recherche et position : affichés dans la barre d'aides en bas de l'écran.</summary>
    public event Action? HintsChanged;

    public string Hints
    {
        get
        {
            if (_inBar)
                return $"{(_lastChip?.Tag is null ? "A:Choisir les catégories" : "A:Filtrer")}|↓:Jeux|Y:Tri : {Filter.SortLabel}|X:Filtres : {Filter.Summary}";
            var categories = CategoryHost.Visibility == Visibility.Visible ? "|LT/RT:Catégorie" : "";
            return $"A:Détails|Y:Tri : {Filter.SortLabel}|X:Filtres : {Filter.Summary}{categories}";
        }
    }

    private void UpdateCount() => HintsChanged?.Invoke();

    private void UpdateEmptyState()
    {
        var empty = _visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        if (!empty) return;
        if (_source.Count == 0)
        {
            EmptyTitle.Text = InstalledOnly ? "Aucun jeu installé détecté" : "Bibliothèque vide";
            EmptyHint.Text = "Le scan des plateformes est peut-être encore en cours.\nAjoute tes clés dans Réglages pour récupérer tes bibliothèques en ligne.";
        }
        else
        {
            EmptyTitle.Text = "Aucun jeu ne correspond";
            EmptyHint.Text = "Appuie sur X pour modifier les filtres.";
        }
    }

    // ---------------- Mise en page ----------------

    // Une seule rangée visible à la fois ; à sa droite, un indicateur de points
    // (le point allongé marque la rangée affichée).
    private int RowCount => (_visible.Count + _cols - 1) / _cols;

    private void Relayout()
    {
        var width = ActualWidth;
        if (width <= 0 || ActualHeight <= 0) return;
        var usable = width - SidePadding * 2;
        // Petit écran (ROG Ally : 1920×1080 à 150 %) : mise en page compacte.
        var compact = ActualHeight < 760;
        ApplyCompact(compact);

        // Deux rangées sous la fiche : la hauteur restante fixe la taille des vignettes, la largeur le nombre de colonnes.
        var bar = CategoryHost.Visibility == Visibility.Visible ? 34 : 0;
        var rowTarget = Math.Min(ActualHeight * 0.3, (ActualHeight - DetailPanel.Height - DetailPanel.Margin.Bottom - 40 - bar) / VisibleRows);
        var targetW = Math.Min(220, (rowTarget - 34) / 1.5);
        var cols = Math.Clamp((int)((usable + Gap) / (targetW + Gap)), 4, 18);
        // Sur un écran très large (limite de colonnes atteinte), les vignettes ne grossissent pas au-delà de ce que permet la hauteur.
        var tileW = Math.Floor(Math.Min((usable - Gap * (cols - 1)) / cols, targetW * 1.15));
        var tileH = Math.Floor(tileW * 1.5);

        TileWidth = tileW;
        TileHeight = tileH;
        TileMargin = new Thickness(0, 0, Gap, 0);
        // Débord de la sélection (zoom, halo) au-delà de la vignette, partagé entre l'écart autour de la rangée et une marge
        // autour de la liste. Cette marge ne dépasse pas l'écart : les rangées hors champ restent invisibles.
        var overflow = Math.Ceiling((tileH * 0.045 + 26) / 2);
        RowHeight = tileH + overflow * 2;
        RowPadding = new Thickness(SidePadding, 0, 0, 0);
        // Un peu moins que l'écart : le bord de la rangée suivante ne dépasse pas d'un filet.
        var edge = overflow - 2;
        RowsList.Height = RowHeight * VisibleRows + edge * 2;
        RowsList.Padding = new Thickness(0, edge, 0, edge);
        RowsList.Margin = new Thickness(0, -edge, 0, -edge);

        if (cols != _cols)
        {
            _cols = cols;
            BuildRows();
        }
        Dispatcher.BeginInvoke(() => ScrollToSelection(false));
    }

    /// <summary>Fiche du jeu plus ramassée sur petit écran : logo et titre réduits, description sur deux lignes.</summary>
    private void ApplyCompact(bool compact)
    {
        LogoBox.Height = compact ? 96 : 150;
        TitleText.FontSize = compact ? 32 : 44;
        TitleText.LineHeight = compact ? 36 : 50;
        MetaRow.Margin = new Thickness(0, compact ? 8 : 16, 0, 0);
        DescText.Margin = new Thickness(0, compact ? 6 : 14, 0, 0);
        DescText.MaxHeight = compact ? 44 : 72;
        DetailPanel.Margin = new Thickness(SidePadding, 0, SidePadding, compact ? 10 : 20);
        // Hauteur fixe (logo + badges + description) : le bloc centré ne saute pas d'un jeu à l'autre.
        DetailPanel.Height = LogoBox.Height + MetaRow.Margin.Top + MetaRow.Height + DescText.Margin.Top + DescText.MaxHeight;
    }

    private void BuildRows()
    {
        var rows = new List<List<object>>();
        for (var i = 0; i < _visible.Count; i += _cols)
        {
            var row = _visible.GetRange(i, Math.Min(_cols, _visible.Count - i)).Cast<object>().ToList();
            // Dernière rangée incomplète : des cartes fantômes complètent la ligne.
            while (row.Count < _cols) row.Add(ShadowTile.Instance);
            rows.Add(row);
        }
        // Une seule rangée : une rangée fantôme en dessous, la grille garde toujours deux lignes.
        if (rows.Count == 1) rows.Add(Enumerable.Repeat<object>(ShadowTile.Instance, _cols).ToList());
        RowsList.ItemsSource = rows;
    }

    // ---------------- Sélection ----------------

    private void Select(int index, bool animate = true, bool force = false)
    {
        if (_visible.Count == 0)
        {
            _index = -1;
            DetailPanel.DataContext = null;
            SelectionChanged?.Invoke(null);
            return;
        }
        index = Math.Clamp(index, 0, _visible.Count - 1);
        if (index == _index && !force) return;

        if (Selected is { } previous) previous.IsSelected = false;
        _index = index;
        var game = _visible[index];
        game.IsSelected = _active && !_inBar && !_suspended;

        DetailPanel.DataContext = game;
        // En défilement rapide, on évite de relancer l'apparition à chaque pas.
        if (DateTime.UtcNow - _lastSelectionChange > TimeSpan.FromMilliseconds(140))
        {
            // Seul le logo glisse ; les informations en dessous apparaissent en simple fondu.
            Anim.Enter(LogoBox, dx: 0, dy: 14, ms: 320);
            foreach (var part in new UIElement[] { MetaRow, DescText })
                part.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Anim.Ms(320)) { EasingFunction = Anim.Out });
        }
        _lastSelectionChange = DateTime.UtcNow;

        ScrollToSelection(animate);
        UpdateCount();
        SelectionChanged?.Invoke(game);
    }

    /// <summary>Deux rangées visibles au plus.</summary>
    private const int VisibleRows = 2;
    private int _firstRow;

    /// <summary>On ne défile que si la sélection sort des deux rangées visibles.</summary>
    private void ScrollToSelection(bool animate)
    {
        _scroller ??= RowsList.Template.FindName("Scroller", RowsList) as ScrollViewer;
        if (_scroller is null || _index < 0) return;
        var row = _index / _cols;
        if (row < _firstRow) _firstRow = row;
        else if (row > _firstRow + VisibleRows - 1) _firstRow = row - VisibleRows + 1;
        _firstRow = Math.Clamp(_firstRow, 0, Math.Max(0, RowCount - VisibleRows));

        var target = _firstRow * RowHeight;
        if (animate) Anim.ScrollTo(_scroller, target, 280);
        else _scroller.ScrollToVerticalOffset(target);
        UpdateDots(row);
    }

    private int _dotsRow = -1, _dotsCount = -1, _dotsStart = -1;

    /// <summary>
    /// Indicateur de rangées : un petit point par rangée, un point allongé pour la rangée visible.
    /// Quand il y a trop de rangées, seule une fenêtre de points autour de la rangée courante est montrée.
    /// </summary>
    private void UpdateDots(int row)
    {
        var rows = RowCount;
        Dots.Visibility = rows > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (rows <= 1) return;

        const double dot = 6, gap = 6, active = 22;
        var max = Math.Max(3, (int)((RowHeight - 16 - active + dot) / (dot + gap)));
        var shown = Math.Min(rows, max);
        var start = Math.Clamp(row - shown / 2, 0, rows - shown);
        var previous = _dotsRow;

        if (rows != _dotsCount || start != _dotsStart || Dots.Children.Count != shown)
        {
            Dots.Children.Clear();
            for (var i = 0; i < shown; i++)
            {
                // Points du bord de la fenêtre plus petits : il reste des rangées au-delà.
                var edge = (i == 0 && start > 0) || (i == shown - 1 && start + shown < rows);
                Dots.Children.Add(new Border
                {
                    Width = edge ? 4 : dot,
                    Height = edge ? 4 : dot,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, gap / 2, 0, gap / 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                });
            }
            _dotsCount = rows;
            _dotsStart = start;
            previous = -1;
        }

        for (var i = 0; i < shown; i++)
        {
            var b = (Border)Dots.Children[i];
            var current = start + i == row;
            var wasCurrent = start + i == previous;
            if (current == wasCurrent && previous >= 0) continue;
            var edge = b.Width < dot;
            var to = current ? active : edge ? 4 : dot;
            b.BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(to, Anim.Ms(220)) { EasingFunction = Anim.Out });
            b.Background = current ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        }
        _dotsRow = row;
    }

    /// <summary>
    /// Les deux onglets partagent les mêmes objets <see cref="Game"/> : seul l'onglet visible
    /// doit marquer sa sélection, sinon elle apparaîtrait aussi dans l'autre.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) _inBar = false;
        if (Selected is { } g) g.IsSelected = active && !_inBar && !_suspended;
        if (active) ReplaySelection();
    }

    private bool _active = true;

    public void SelectGame(Game game)
    {
        var idx = _visible.IndexOf(game);
        if (idx >= 0) Select(idx);
    }

    // ---------------- Entrées ----------------

    public bool Handle(NavAction action)
    {
        if (_inBar)
            switch (action)
            {
                case NavAction.Left or NavAction.Right:
                    // Du premier bouton, gauche va vers la barre de gauche.
                    return MoveInBar(action == NavAction.Left ? -1 : 1) || action == NavAction.Right;
                case NavAction.Up:
                    return true;
                case NavAction.Down or NavAction.Back:
                    LeaveBar();
                    return true;
                case NavAction.Accept:
                    FocusNav.Activate();
                    return true;
            }

        switch (action)
        {
            // Droite continue sur la rangée suivante ; gauche, sur la première colonne, va vers la barre de gauche.
            case NavAction.Left:
                if (_index <= 0 || _index % _cols == 0) return false;
                Select(_index - 1);
                return true;
            case NavAction.Right:
                if (_index + 1 < _visible.Count) Select(_index + 1);
                return true;
            case NavAction.Up:
                // De la première rangée, haut monte vers les catégories.
                if (_index - _cols >= 0) Select(_index - _cols);
                else EnterBar();
                return true;
            case NavAction.Down:
                if (_index / _cols < (_visible.Count - 1) / _cols) Select(Math.Min(_index + _cols, _visible.Count - 1));
                return true;
            case NavAction.Accept:
                if (Selected is { } g) OpenRequested?.Invoke(g);
                return true;
            case NavAction.Option:
                FilterRequested?.Invoke();
                return true;
            case NavAction.Secondary:
                SortRequested?.Invoke();
                return true;
            case NavAction.PrevLetter:
                CycleCategory(-1);
                return true;
            case NavAction.NextLetter:
                CycleCategory(+1);
                return true;
        }
        return false;
    }


    // ---------------- Catégories ----------------

    /// <summary>Boutons affichés au-dessus de la grille, puis le bouton + tout à droite.</summary>
    public void SetCategories(IReadOnlyList<string> keys)
    {
        var chips = keys.Select(key => Chip(GameCategories.Label(key), key)).Prepend(Chip("Toutes", AllCategories)).ToList();
        var plus = Chip("", null);
        plus.FontFamily = (FontFamily)FindResource("Icons");
        plus.Margin = new Thickness(0);
        CategoryBar.Children.Clear();
        foreach (var c in chips) CategoryBar.Children.Add(c);
        CategoryBar.Children.Add(plus);
        CategoryHost.Visibility = Visibility.Visible;
        _categoryHighlight ??= new SlideHighlight(CategoryHighlight, CategoryHost);
        if (_categoryPill is null)
        {
            _categoryPill = new SlideHighlight(CategoryPill, CategoryHost);
            // Page affichée ou taille de l'interface changée : le fond reprend sa place.
            CategoryHost.IsVisibleChanged += (_, _) => MoveCategoryPill();
            CategoryBar.SizeChanged += (_, _) => MoveCategoryPill();
        }
        UpdateCategoryBar();
        Relayout();
    }

    /// <summary>Clé du bouton « Toutes » : aucun filtre de catégorie.</summary>
    private const string AllCategories = "";
    private string ActiveChip => Filter.Category ?? AllCategories;

    private Button Chip(string label, string? key)
    {
        var chip = new Button { Content = label, Tag = key, Style = (Style)FindResource("CategoryChip") };
        chip.Click += (_, _) =>
        {
            if (key is null) { CategoriesRequested?.Invoke(); return; }
            // Bouton déjà actif : on revoit tous les jeux.
            Filter.Category = key == AllCategories || Filter.Category == key ? null : key;
            Refresh(toTop: true);
        };
        chip.GotKeyboardFocus += (_, _) =>
        {
            _lastChip = chip;
            _categoryHighlight?.MoveTo(chip);
            if (!_inBar)
            {
                _inBar = true;
                if (Selected is { } g) g.IsSelected = false;
            }
            HintsChanged?.Invoke();
        };
        return chip;
    }

    private void PrevCategory_Click(object sender, MouseButtonEventArgs e) => CycleCategory(-1);
    private void NextCategory_Click(object sender, MouseButtonEventArgs e) => CycleCategory(+1);

    /// <summary>LT / RT : catégorie précédente ou suivante, en boucle (« Toutes » compris).</summary>
    private void CycleCategory(int direction)
    {
        if (CategoryHost.Visibility != Visibility.Visible) return;
        var chips = CategoryBar.Children.OfType<Button>().Where(c => c.Tag is string).ToList();
        if (chips.Count == 0) return;
        var next = (chips.FindIndex(c => (string)c.Tag == ActiveChip) + direction + chips.Count) % chips.Count;
        Filter.Category = (string)chips[next].Tag == AllCategories ? null : (string)chips[next].Tag;
        Refresh(toTop: true);
        chips[next].BringIntoView();
    }

    private void UpdateCategoryBar()
    {
        var inactive = ((SolidColorBrush)FindResource("TextSecondary")).Color;
        foreach (var chip in CategoryBar.Children.OfType<Button>())
        {
            var active = chip.Tag is string key && key == ActiveChip;
            chip.Background = active ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            // Le texte change de couleur pendant que le fond arrive.
            var text = new SolidColorBrush(chip.Foreground is SolidColorBrush current ? current.Color : inactive);
            chip.Foreground = text;
            text.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(active ? Color.FromRgb(0x0A, 0x0C, 0x11) : inactive, Anim.Ms(170)));
        }
        Dispatcher.BeginInvoke(MoveCategoryPill, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MoveCategoryPill()
    {
        if (_categoryPill is null || !CategoryHost.IsVisible) return;
        var active = CategoryBar.Children.OfType<Button>().FirstOrDefault(c => c.Tag is string key && key == ActiveChip);
        if (active is null) _categoryPill.Hide();
        else _categoryPill.MoveTo(active);
    }

    private void EnterBar()
    {
        if (CategoryHost.Visibility != Visibility.Visible || CategoryBar.Children.Count == 0) return;
        _inBar = true;
        if (Selected is { } g) g.IsSelected = false;
        FocusBar();
    }

    /// <summary>Sélection sur le bouton actif, sinon le dernier utilisé, sinon le premier.</summary>
    private void FocusBar()
    {
        var chips = CategoryBar.Children.OfType<Button>().ToList();
        var target = chips.FirstOrDefault(c => c == _lastChip) ?? chips.FirstOrDefault(c => c.Tag is string key && key == ActiveChip) ?? chips.FirstOrDefault();
        if (target is not null) FocusNav.Focus(target);
    }

    private bool MoveInBar(int direction)
    {
        var chips = CategoryBar.Children.OfType<Button>().ToList();
        var i = chips.IndexOf(Keyboard.FocusedElement as Button ?? chips[0]);
        if (i + direction < 0 || i + direction >= chips.Count) return false;
        FocusNav.Focus(chips[i + direction]);
        return true;
    }

    private bool _suspended;

    /// <summary>La barre de gauche a la main : plus de vignette en surbrillance, qui ferait une deuxième sélection à l'écran.</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (Selected is { } g) g.IsSelected = _active && !_inBar && !suspended;
    }

    private void LeaveBar()
    {
        if (_visible.Count == 0) return; // aucun jeu où aller : la sélection reste sur les boutons
        _inBar = false;
        _categoryHighlight?.Hide();
        Window.GetWindow(this)?.Focus();
        if (Selected is { } g) g.IsSelected = _active;
        HintsChanged?.Invoke();
    }

    /// <summary>Retour d'un panneau : la sélection reprend sa place sur les boutons si elle y était.</summary>
    public void RestoreFocus()
    {
        if (_inBar) Dispatcher.BeginInvoke(FocusBar, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Game game) return;
        if (game == Selected) OpenRequested?.Invoke(game);
        else SelectGame(game);
    }
}

/// <summary>Emplacement vide d'une rangée de vignettes.</summary>
public sealed class ShadowTile
{
    public static readonly ShadowTile Instance = new();
    private ShadowTile() { }
}

/// <summary>Vignette de jeu, ou carte fantôme pour un emplacement vide.</summary>
public sealed class TileSelector : DataTemplateSelector
{
    public DataTemplate? Tile { get; set; }
    public DataTemplate? Shadow { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is ShadowTile ? Shadow : Tile;
}
