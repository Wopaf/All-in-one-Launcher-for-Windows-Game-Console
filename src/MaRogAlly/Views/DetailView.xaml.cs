using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Page d'un jeu : actions (jouer, dossier…), description complète, fiche technique, succès mis en avant, captures.</summary>
public partial class DetailView : OverlayBase
{
    public Game? Game { get; private set; }

    public event Action<Game>? PlayRequested, FolderRequested, HideRequested, RefreshRequested, PlatformRequested, UninstallRequested;
    /// <summary>Mode magasin (jeu non possédé, depuis la Découverte) : page Steam ou recherche Instant Gaming.</summary>
    public event Action<DiscoverGame>? SteamRequested, InstantGamingRequested, WishRequested;
    /// <summary>Section Succès validée : les succès de ce jeu dans l'onglet Succès.</summary>
    public event Action<AchievementGame>? AchievementsRequested;

    /// <summary>Jeu du magasin affiché (jeu non possédé), ou null pour un jeu de la bibliothèque.</summary>
    private DiscoverGame? _store;

    /// <summary>Passe la fiche en mode magasin : prix, avis, et liens vers Steam et Instant Gaming au lieu des actions de jeu.</summary>
    public void SetStore(DiscoverGame store)
    {
        _store = store;
        SteamLogo.Data = System.Windows.Media.Geometry.Parse(PlatformInfo.Of(Platform.Steam).Logo);
        UpdateState();
    }

    public static readonly DependencyProperty ShotWidthProperty = DependencyProperty.Register(nameof(ShotWidth), typeof(double), typeof(DetailView), new PropertyMetadata(240.0));
    public static readonly DependencyProperty ShotHeightProperty = DependencyProperty.Register(nameof(ShotHeight), typeof(double), typeof(DetailView), new PropertyMetadata(135.0));
    public double ShotWidth { get => (double)GetValue(ShotWidthProperty); set => SetValue(ShotWidthProperty, value); }
    public double ShotHeight { get => (double)GetValue(ShotHeightProperty); set => SetValue(ShotHeightProperty, value); }

    /// <summary>Fiche plus ramassée sur petit écran (ROG Ally).</summary>
    private void ApplyCompact(bool compact)
    {
        // Centré verticalement ; horizontalement, une marge droite plus large garde le bloc un peu à gauche du centre.
        var right = 64 + ActualWidth * 0.2;
        Body.Margin = compact ? new Thickness(64, 36, right, 36) : new Thickness(64, 56, right, 56);
        _compact = compact;
        LogoBox.Height = compact ? 120 : 190;
        TitleText.FontSize = compact ? 34 : 50;
        // Sections bien séparées : logo, infos, actions, description, captures.
        // Avec la section Succès, les espaces se resserrent : tout doit tenir sans défiler sur petit écran.
        var tight = compact && _achievementGame is not null;
        MetaRow.Margin = new Thickness(0, tight ? 12 : compact ? 18 : 26, 0, 0);
        Actions.Margin = new Thickness(0, tight ? 18 : compact ? 24 : 34, 0, 0);
        DescScroll.Margin = new Thickness(0, tight ? 24 : compact ? 38 : 52, 0, 0);
        MoreButton.Margin = new Thickness(0, compact ? 14 : 18, 0, 0);
        DescText.FontSize = compact ? 15 : 17;
        DescText.LineHeight = compact ? 23 : 27;
        ApplyDescription();
        Gallery.Margin = new Thickness(0, tight ? 18 : compact ? 26 : 34, 0, 0);
        ShotWidth = compact ? 160 : 240;
        ShotHeight = compact ? 90 : 135;
        InfoSection.Margin = new Thickness(0, tight ? 18 : compact ? 26 : 34, 0, 0);
        InfoSection.Padding = new Thickness(0, compact ? 18 : 24, 0, 0);
    }

    public DetailView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyCompact(ActualHeight < 860);
        DescText.SizeChanged += (_, _) => UpdateMoreButton();
        foreach (var b in Actions.Children.OfType<Button>())
            b.GotKeyboardFocus += (_, _) => ActionLabel.Text = b.ToolTip as string ?? "";
        PlayButton.GotKeyboardFocus += (_, _) =>
        {
            ActionLabel.Text = "";
            SweepPlayButton();
        };
    }

    /// <summary>Rejoue le trait du bouton Jouer (« --sweep », pour vérifier l'animation).</summary>
    public void PreviewSweep() => SweepPlayButton();

    /// <summary>Un trait lumineux parcourt une fois tout le contour du bouton Jouer / Installer.</summary>
    private void SweepPlayButton()
    {
        var w = PlayButton.ActualWidth;
        var h = PlayButton.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            // Fiche tout juste ouverte : le bouton n'est pas encore mesuré.
            Dispatcher.BeginInvoke(() => { if (PlayButton.ActualWidth > 0) SweepPlayButton(); }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        PlaySweep.Width = w;
        PlaySweep.Height = h;
        PlaySweep.RadiusX = PlaySweep.RadiusY = h / 2;

        // Les tirets se comptent en épaisseurs de trait : un seul trait (≈ 30 % du tour), puis un vide d'un tour entier.
        var t = PlaySweep.StrokeThickness;
        var perimeter = 2 * (w - h) + Math.PI * h;
        PlaySweep.StrokeDashArray = new System.Windows.Media.DoubleCollection { perimeter * 0.3 / t, perimeter / t };

        const double ms = 700;
        PlaySweep.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, -perimeter / t, Anim.Ms(ms)) { EasingFunction = Anim.InOut });
        PlaySweep.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms - 150))),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))),
            },
        });
    }

    // ---------------- Description repliable ----------------

    private bool _compact, _expanded;

    /// <summary>Hauteur de la description repliée : quelques lignes.</summary>
    private double CollapsedHeight => DescText.LineHeight * (_compact ? 3 : 4);

    private void ApplyDescription()
    {
        DescScroll.MaxHeight = _expanded ? double.PositiveInfinity : CollapsedHeight;
        // Dépliée, la description prend la place des captures.
        DescRow.Height = _expanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        // Repliée : le tout est centré verticalement ; dépliée : la page entière sert à la lecture.
        Body.VerticalAlignment = _expanded ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        Gallery.Visibility = _expanded || Shots.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AchievementSection.Visibility = _expanded || _achievementGame is null ? Visibility.Collapsed : Visibility.Visible;
        MoreText.Text = _expanded ? "Réduire" : "Lire la suite";
        MoreIcon.Text = _expanded ? "\uE70E" : "\uE70D";
        UpdateMoreButton();
    }

    private void UpdateMoreButton()
        => MoreButton.Visibility = _expanded || DescText.ActualHeight > CollapsedHeight + 2 ? Visibility.Visible : Visibility.Collapsed;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyDescription();
        if (!_expanded) DescScroll.ScrollToTop();
        Anim.Enter(DescScroll, 0, _expanded ? 8 : -8, 260);
        RaiseHintsChanged();
    }

    // ---------------- Visionneuse de captures ----------------

    private const int ShotsShown = 5;
    private List<string> _shotUrls = [];
    private int _shotIndex = -1;
    private bool LightboxOpen => _shotIndex >= 0;

    private void Shot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BitmapImage { UriSource: { } uri })
            OpenShot(Math.Max(0, _shotUrls.IndexOf(uri.OriginalString)));
    }

    private void OpenShot(int index)
    {
        if (_shotUrls.Count == 0) return;
        var first = !LightboxOpen;
        _shotIndex = (index + _shotUrls.Count) % _shotUrls.Count;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(_shotUrls[_shotIndex]);
        bmp.DecodePixelWidth = 1920;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        Lightbox.Visibility = Visibility.Visible;
        if (first) BuildStrip();
        UpdateStrip(animate: !first);
        if (first)
        {
            // Ouverture : l'image apparaît avec la visionneuse.
            LightboxA.Source = bmp;
            LightboxA.BeginAnimation(OpacityProperty, null);
            LightboxA.Opacity = 1;
            LightboxB.BeginAnimation(OpacityProperty, null);
            LightboxB.Opacity = 0;
            _lightboxOnA = true;
            Lightbox.Opacity = 0;
            Anim.Fade(Lightbox, 1, 220);
            RaiseHintsChanged();
            return;
        }
        // Changement d'image : fondu enchaîné entre les deux calques.
        var incoming = _lightboxOnA ? LightboxB : LightboxA;
        var outgoing = _lightboxOnA ? LightboxA : LightboxB;
        incoming.Source = bmp;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        incoming.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Anim.Ms(320)) { EasingFunction = Anim.InOut });
        outgoing.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, Anim.Ms(320)) { EasingFunction = Anim.InOut });
        _lightboxOnA = !_lightboxOnA;
    }

    private bool _lightboxOnA = true;

    // Vignettes du carrousel sous l'image agrandie.
    private const double StripWidth = 112, StripHeight = 63, StripGap = 10;

    private void BuildStrip()
    {
        Strip.Children.Clear();
        for (var i = 0; i < _shotUrls.Count; i++)
        {
            var index = i;
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.UriSource = new Uri(_shotUrls[i]);
            thumb.DecodePixelWidth = 240;
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.EndInit();
            var image = new Image { Source = thumb, Stretch = System.Windows.Media.Stretch.UniformToFill };
            Rounded.SetRadius(image, 8);
            var frame = new Border
            {
                Width = StripWidth,
                Height = StripHeight,
                Margin = new Thickness(StripGap / 2, 0, StripGap / 2, 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Cursor = Cursors.Hand,
                Child = image,
            };
            frame.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenShot(index); };
            Strip.Children.Add(frame);
        }
    }

    /// <summary>
    /// Le cadre blanc glisse jusqu'à la capture affichée, qui s'éclaire et grossit légèrement ;
    /// les autres s'atténuent. Le carrousel défile pour la garder visible.
    /// </summary>
    private void UpdateStrip(bool animate)
    {
        StripFrame.Width = StripWidth;
        StripFrame.Height = StripHeight;
        var frameX = _shotIndex * (StripWidth + StripGap) + StripGap / 2;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        if (animate) StripFrameMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(frameX, Anim.Ms(260)) { EasingFunction = ease });
        else { StripFrameMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null); StripFrameMove.X = frameX; }

        for (var i = 0; i < Strip.Children.Count; i++)
        {
            var frame = (Border)Strip.Children[i];
            var current = i == _shotIndex;
            Anim.Fade(frame, current ? 1 : 0.45, 220);
            if (frame.RenderTransform is not System.Windows.Media.ScaleTransform scale)
            {
                frame.RenderTransformOrigin = new Point(0.5, 0.5);
                frame.RenderTransform = scale = new System.Windows.Media.ScaleTransform(1, 1);
            }
            var to = current ? 1.06 : 1.0;
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(to, Anim.Ms(260)) { EasingFunction = ease });
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(to, Anim.Ms(260)) { EasingFunction = ease });
        }
        var x = _shotIndex * (StripWidth + StripGap) - (StripScroll.ViewportWidth - StripWidth) / 2;
        Anim.ScrollToH(StripScroll, Math.Max(0, x));
    }

    /// <summary>Ouvre la visionneuse sur la première capture (« --shot », pour vérifier l'affichage).</summary>
    public void PreviewShots() => OpenShot(0);

    private void CloseShot()
    {
        // Retour sur la capture correspondante (la dernière vignette si on a parcouru plus loin).
        var index = Math.Min(_shotIndex, Shots.Items.Count - 1);
        _shotIndex = -1;
        Lightbox.Visibility = Visibility.Collapsed;
        LightboxA.Source = LightboxB.Source = null;
        if (Shots.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container && FindButton(container) is { } b)
            FocusNav.Focus(b);
        RaiseHintsChanged();
    }

    private static Button? FindButton(DependencyObject o)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
            if (child is Button b) return b;
            if (FindButton(child) is { } found) return found;
        }
        return null;
    }

    private void Lightbox_Click(object sender, MouseButtonEventArgs e) => CloseShot();

    protected override FrameworkElement AnimatedPart => Body;
    protected override double EnterFromX => 0;
    protected override FrameworkElement FocusScope => Body;

    public void SetGame(Game game)
    {
        Game = game;
        DataContext = game;
        UpdateState();

        _shotUrls = game.Meta?.Screenshots.ToList() ?? [];
        var shots = _shotUrls.Take(ShotsShown).Select(url =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.DecodePixelWidth = 480;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            return bmp;
        }).ToList() ?? [];
        Shots.ItemsSource = shots;
        _expanded = false;
        ApplyDescription();
        DescScroll.ScrollToTop();
    }

    /// <summary>
    /// Fiche magasin encore incomplète : des cartes fantômes qui respirent à la place des captures,
    /// remplacées par les vraies dès que la fiche arrive (<see cref="SetGame"/>).
    /// </summary>
    public void ShowLoadingShots()
    {
        if (Shots.Items.Count > 0) return;
        Shots.ItemsSource = Enumerable.Repeat<object?>(null, ShotsShown).ToList();
        ApplyDescription();
    }

    /// <summary>La fiche n'a pas pu être complétée : on retire les cartes fantômes.</summary>
    public void ClearLoadingShots()
    {
        if (_shotUrls.Count > 0) return;
        Shots.ItemsSource = null;
        ApplyDescription();
    }

    // ---------------- Succès mis en avant ----------------

    private AchievementGame? _achievementGame;

    /// <summary>Succès du jeu (null : aucun, la section disparaît). Rien pour un jeu du magasin.</summary>
    public void SetAchievements(AchievementGame? game, Func<string?, Task<string?>> iconLoader)
    {
        if (_store is not null) return;
        _achievementGame = game is { Total: > 0 } ? game : null;
        ApplyCompact(_compact);
        if (_achievementGame is not { } g) return;

        AchievementCount.Text = $"{g.Unlocked} / {g.Total}  ·  {g.Ratio:P0}";
        AchievementCount.Foreground = (System.Windows.Media.Brush)FindResource(g.Completed ? "Gold" : "TextPrimary");
        AchievementFill.Width = AchievementBar.Width * g.Ratio;
        AchievementFill.Background = g.Completed
            ? (System.Windows.Media.Brush)FindResource("Gold")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

        AchievementCards.Children.Clear();
        var highlights = PickHighlights(g);
        AchievementCards.Visibility = highlights.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (label, a) in highlights) AchievementCards.Children.Add(BuildAchievementCard(label, a, iconLoader));
    }

    /// <summary>Le plus rare obtenu, le dernier obtenu, le prochain à viser ; à défaut, les suivants pour faire trois.</summary>
    private static List<(string Label, Achievement Item)> PickHighlights(AchievementGame g)
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        string Percent(Achievement a) => a.Rarity is { } r ? $"{r.ToString(r < 10 ? "0.#" : "0", fr)} %" : "";
        string With(string label, string detail) => detail.Length > 0 ? $"{label} · {detail}" : label;

        var picks = new List<(string Label, Achievement Item)>();
        var unlocked = g.Items.Where(a => a.Unlocked).ToList();
        var locked = g.Items.Where(a => !a.Unlocked).ToList();
        if (unlocked.Where(a => a.Rarity is not null).MinBy(a => a.Rarity) is { } rare)
            picks.Add((With("Le plus rare", Percent(rare)), rare));
        if (unlocked.Where(a => a.UnlockedAt is not null && picks.All(p => p.Item != a)).MaxBy(a => a.UnlockedAt) is { } last)
            picks.Add((With("Le dernier", last.UnlockedAt!.Value.ToString("d MMM yyyy", fr)), last));
        // Le plus courant parmi ceux qui restent : le plus accessible.
        if (locked.Where(a => !a.Hidden).MaxBy(a => a.Rarity ?? -1) is { } next)
            picks.Add((With("À viser", Percent(next)), next));
        foreach (var a in unlocked.OrderBy(a => a.Rarity ?? double.MaxValue).Concat(locked.OrderByDescending(a => a.Rarity ?? -1)))
        {
            if (picks.Count >= 3) break;
            if (picks.Any(p => p.Item == a)) continue;
            picks.Add((With(a.Unlocked ? "Obtenu" : "À débloquer", Percent(a)), a));
        }
        return picks;
    }

    private Border BuildAchievementCard(string label, Achievement a, Func<string?, Task<string?>> iconLoader)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var frame = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1F, 0x2A)) };
        Rounded.SetRadius(frame, 7);
        var icon = new Image { Stretch = System.Windows.Media.Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(icon, 80);
        // L'atténuation est portée par un parent : le fondu d'apparition remet l'opacité de l'image à 1.
        frame.Children.Add(new Grid { Opacity = a.Unlocked ? 1 : 0.45, Children = { icon } });
        grid.Children.Add(frame);
        LoadAchievementIcon(icon, a.Unlocked ? a.Icon : a.IconLocked ?? a.Icon, iconLoader);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource(a.Unlocked ? "TextSecondary" : "TextMuted"),
        });
        text.Children.Add(new TextBlock
        {
            Text = a.Hidden && !a.Unlocked ? "Succès secret" : a.Name, FontSize = 14, Margin = new Thickness(0, 2, 0, 0),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource(a.Unlocked ? "TextPrimary" : "TextSecondary"),
        });
        grid.Children.Add(text);

        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0), Child = grid,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
    }

    private static async void LoadAchievementIcon(Image image, string? url, Func<string?, Task<string?>> iconLoader)
    {
        if (url is null) return;
        if (await iconLoader(url) is { } path) ImageLoader.SetSource(image, path);
    }

    private void AchievementSection_Click(object sender, RoutedEventArgs e)
    {
        if (_achievementGame is { } g) AchievementsRequested?.Invoke(g);
    }

    /// <summary>Met à jour libellés et icônes après une action (masquer…).</summary>
    public void UpdateState()
    {
        if (Game is not { } g) return;
        if (_store is { } store)
        {
            ShowStore(g, store);
            return;
        }
        PlayText.Text = g.IsInstalled ? "Jouer" : "Installer";
        PlayIcon.Text = g.IsInstalled ? "" : "";
        HideIcon.Text = g.IsHidden ? "" : "";
        FolderButton.Visibility = g.IsInstalled && g.InstallDir is not null ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = g.IsInstalled && g.Platform != Platform.Manual ? Visibility.Visible : Visibility.Collapsed;
        PlatformButton.Visibility = GameLauncher.HasPlatformPage(g) ? Visibility.Visible : Visibility.Collapsed;
        PlatformButton.ToolTip = $"Voir dans {g.PlatformInfo.Name}";

        InfoList.Children.Clear();
        // Plateforme, genres et temps de jeu figurent déjà dans les badges sous le logo.
        AddInfo("Développeur", g.Meta?.Developer);
        AddInfo("Éditeur", g.Meta?.Publisher);
        AddInfo("Sortie", g.Meta?.ReleaseDate?.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("fr-FR")));
        AddInfo("Dernière partie", g.LastPlayedAny is { } last
            ? last.ToString("d MMM yyyy, HH:mm", new System.Globalization.CultureInfo("fr-FR"))
            : "Jamais joué");
    }

    /// <summary>Jeu non possédé : seuls les liens vers les magasins, et une fiche technique orientée achat.</summary>
    private void ShowStore(Game g, DiscoverGame store)
    {
        foreach (UIElement child in Actions.Children)
            child.Visibility = child == SteamButton || child == InstantGamingButton || child == WishButton ? Visibility.Visible : Visibility.Collapsed;
        SteamButton.Margin = new Thickness(0, 0, 12, 0);
        InstantGamingButton.Margin = new Thickness(0);

        var fr = new System.Globalization.CultureInfo("fr-FR");
        InfoList.Children.Clear();
        AddInfo("Prix", store.PriceText is { } price
            ? store.DiscountText is { } d ? $"{price}  ({d}{(store.OriginalPrice is { } o ? $", au lieu de {o}" : "")})" : price
            : store.ComingSoon ? "Pas encore en vente" : null);
        AddInfo("Avis", store.ReviewText);
        AddInfo("Joueurs", store.PlayersText);
        AddInfo("Sortie", g.Meta?.ReleaseDate?.ToString("d MMMM yyyy", fr) ?? store.ReleaseText);
        AddInfo("Développeur", g.Meta?.Developer);
        AddInfo("Éditeur", g.Meta?.Publisher);
    }

    private static readonly Dictionary<string, string> InfoIcons = new()
    {
        ["Prix"] = "\uE7BF", ["Avis"] = "\uE8E1", ["Joueurs"] = "\uE716",
        ["Développeur"] = "\uE943", ["Éditeur"] = "\uE8F1", ["Sortie"] = "\uE787",
        ["Dernière partie"] = "\uE81C",
    };

    /// <summary>Une info de la fiche technique : icône à la couleur d'accent, intitulé discret, valeur.</summary>
    private void AddInfo(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var compact = Application.Current.MainWindow?.ActualHeight is > 0 and < 800;
        var grid = new Grid { Width = compact ? 200 : 215, Margin = new Thickness(0, 0, 0, compact ? 14 : 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock
        {
            Text = InfoIcons.GetValueOrDefault(label, "\uE946"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"),
            FontSize = 15,
            Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = (Style)FindResource("Label"), FontSize = 11 });
        text.Children.Add(new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 3, 12, 0),
            FontSize = compact ? 14 : 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
        });
        grid.Children.Add(text);
        InfoList.Children.Add(grid);
    }

    public override bool Handle(NavAction action)
    {
        if (LightboxOpen)
        {
            switch (action)
            {
                case NavAction.Left: OpenShot(_shotIndex - 1); break;
                case NavAction.Right: OpenShot(_shotIndex + 1); break;
                case NavAction.Back or NavAction.Accept: CloseShot(); break;
            }
            return true;
        }

        // Description dépliée : haut / bas la font défiler tant qu'il reste du texte.
        if (_expanded && MoreButton.IsKeyboardFocused)
        {
            const double step = 140;
            if (action == NavAction.Down && DescScroll.VerticalOffset < DescScroll.ScrollableHeight)
            {
                Anim.ScrollTo(DescScroll, DescScroll.VerticalOffset + step, 220);
                return true;
            }
            if (action == NavAction.Up && DescScroll.VerticalOffset > 0)
            {
                Anim.ScrollTo(DescScroll, DescScroll.VerticalOffset - step, 220);
                return true;
            }
        }

        switch (action)
        {
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                MoveFocus(action);
                return true;
        }
        return base.Handle(action);
    }

    /// <summary>
    /// Rangées de la fiche, de haut en bas : actions, « Lire la suite », succès, captures.
    /// (La prédiction de WPF ne trouvait pas toujours la rangée du dessous.)
    /// </summary>
    private List<List<Button>> NavRows()
    {
        var actions = new List<Button>();
        foreach (UIElement child in Actions.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (child == PlayBox) actions.Add(PlayButton);
            else if (child is Button b) actions.Add(b);
        }
        var rows = new List<List<Button>> { actions };
        if (MoreButton.Visibility == Visibility.Visible) rows.Add([MoreButton]);
        if (AchievementSection.Visibility == Visibility.Visible) rows.Add([AchievementSection]);
        if (Gallery.Visibility == Visibility.Visible)
        {
            rows.Add(Enumerable.Range(0, Shots.Items.Count)
                .Select(i => Shots.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c ? FindButton(c) : null)
                .OfType<Button>().ToList());
        }
        return rows.Where(r => r.Count > 0).ToList();
    }

    private void MoveFocus(NavAction action)
    {
        var rows = NavRows();
        if (rows.Count == 0) return;
        var focused = Keyboard.FocusedElement as Button;
        var row = rows.FindIndex(r => r.Contains(focused!));
        if (focused is null || row < 0)
        {
            FocusNav.Focus(rows[0][0]);
            return;
        }
        var col = rows[row].IndexOf(focused);
        switch (action)
        {
            case NavAction.Left when col > 0:
                FocusNav.Focus(rows[row][col - 1]);
                break;
            case NavAction.Right when col < rows[row].Count - 1:
                FocusNav.Focus(rows[row][col + 1]);
                break;
            case NavAction.Up when row > 0:
                FocusNav.Focus(Nearest(rows[row - 1], focused));
                break;
            case NavAction.Down when row < rows.Count - 1:
                FocusNav.Focus(Nearest(rows[row + 1], focused));
                break;
        }
    }

    /// <summary>L'élément de la rangée le plus proche horizontalement de celui qui a le focus.</summary>
    private Button Nearest(List<Button> row, Button from)
    {
        double CenterX(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), Body).X;
        var x = CenterX(from);
        return row.MinBy(b => Math.Abs(CenterX(b) - x))!;
    }

    public override string Hints => LightboxOpen ? "←→:Image|B:Fermer"
        : _store is not null ? (_expanded ? "A:Valider|↕:Défiler|B:Retour" : "A:Valider|B:Retour")
        : _expanded ? "A:Valider|↕:Défiler|B:Retour"
        : "A:Valider|B:Retour";

    /// <summary>Fiche magasin : état du bouton de la liste de souhaits.</summary>
    public void SetWished(bool wished)
    {
        WishText.Text = wished ? "Dans ma liste" : "Ajouter à ma liste";
        WishIcon.Text = wished ? "" : "";
    }

    private void Wish_Click(object sender, RoutedEventArgs e) { if (_store is { } s) WishRequested?.Invoke(s); }
    private void Steam_Click(object sender, RoutedEventArgs e) { if (_store is { } s) SteamRequested?.Invoke(s); }
    private void InstantGaming_Click(object sender, RoutedEventArgs e) { if (_store is { } s) InstantGamingRequested?.Invoke(s); }

    private void Play_Click(object sender, RoutedEventArgs e) { if (Game is { } g) PlayRequested?.Invoke(g); }
    private void Uninstall_Click(object sender, RoutedEventArgs e) { if (Game is { } g) UninstallRequested?.Invoke(g); }
    private void Platform_Click(object sender, RoutedEventArgs e) { if (Game is { } g) PlatformRequested?.Invoke(g); }
    private void Folder_Click(object sender, RoutedEventArgs e) { if (Game is { } g) FolderRequested?.Invoke(g); }
    private void Hide_Click(object sender, RoutedEventArgs e) { if (Game is { } g) HideRequested?.Invoke(g); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (Game is { } g) RefreshRequested?.Invoke(g); }
}
