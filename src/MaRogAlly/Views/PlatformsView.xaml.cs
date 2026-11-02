using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Toutes les plateformes, installées ou non : liste à gauche, vitrine de la plateforme
/// sélectionnée à droite (logo, chiffres clés, aperçu des jeux, accès au lanceur).
/// </summary>
public partial class PlatformsView : UserControl
{
    private List<PlatformStatus> _statuses = [];
    private List<Game> _games = [];
    private PlatformStatus? _current;

    public static readonly DependencyProperty CoverWidthProperty = DependencyProperty.Register(nameof(CoverWidth), typeof(double), typeof(PlatformsView), new PropertyMetadata(116.0));
    public static readonly DependencyProperty CoverHeightProperty = DependencyProperty.Register(nameof(CoverHeight), typeof(double), typeof(PlatformsView), new PropertyMetadata(174.0));
    public double CoverWidth { get => (double)GetValue(CoverWidthProperty); set => SetValue(CoverWidthProperty, value); }
    public double CoverHeight { get => (double)GetValue(CoverHeightProperty); set => SetValue(CoverHeightProperty, value); }

    /// <summary>Vitrine plus ramassée sur petit écran (ROG Ally), pour tout afficher sans défiler.</summary>
    private void ApplyCompact(bool compact)
    {
        ListTitle.FontSize = compact ? 28 : 34;
        // Logo seul, sans cadre, un peu détaché du haut de l'écran.
        Showcase.Margin = new Thickness(48, compact ? 26 : 44, 0, 40);
        LogoBadge.Width = LogoBadge.Height = BigLogo.Width = BigLogo.Height = compact ? 58 : 88;
        NameText.FontSize = compact ? 36 : 58;
        NameText.Margin = new Thickness(0, compact ? 10 : 26, 0, 0);
        TaglineText.FontSize = compact ? 15 : 19;
        StatusChip.Margin = new Thickness(0, compact ? 10 : 20, 0, 0);
        StatsGrid.Margin = new Thickness(0, compact ? 14 : 34, 0, 0);
        foreach (var t in new[] { InstalledCount, OwnedCount, HoursCount }) t.FontSize = compact ? 30 : 44;
        RatioBar.Margin = new Thickness(0, compact ? 10 : 22, 0, 0);
        GamesLabel.Margin = new Thickness(0, compact ? 14 : 34, 0, compact ? 8 : 12);
        CoverWidth = compact ? 74 : 116;
        CoverHeight = compact ? 111 : 174;
        ActionsRow.Margin = new Thickness(0, compact ? 14 : 34, 0, 0);
    }

    public PlatformsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyCompact(ActualHeight < 760);
        SetData(PlatformInfo.All.Where(p => p.Id != Platform.Manual).Select(p => new PlatformStatus(p, false, 0, 0)).ToList(), []);
    }

    public void SetData(List<PlatformStatus> statuses, IReadOnlyList<Game> games)
    {
        var focused = (Keyboard.FocusedElement as Button)?.Tag as PlatformStatus;
        _games = games.ToList();
        _statuses = statuses
            .Where(s => s.Info.Id != Platform.Manual || s.Owned > 0)
            .OrderByDescending(s => s.ClientInstalled)
            .ThenByDescending(s => s.Owned)
            .ThenBy(s => (int)s.Info.Id)
            .ToList();

        List.Children.Clear();
        _rowStates.Clear();
        _running = _statuses.Where(s => s.ClientInstalled && s.Info.IsLauncherRunning).Select(s => s.Info.Id).ToHashSet();
        AddGroup("Lanceurs installés", _statuses.Where(s => s.ClientInstalled));
        AddGroup("Non installés", _statuses.Where(s => !s.ClientInstalled));

        var installed = _statuses.Count(s => s.ClientInstalled);
        Summary.Text = $"{installed} sur {_statuses.Count} installées · {_statuses.Sum(s => s.Owned)} jeux";

        var again = List.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as PlatformStatus)?.Info.Id == focused?.Info.Id);
        if (again is not null) again.Focus();
        Show(_statuses.FirstOrDefault(s => s.Info.Id == (focused ?? _current)?.Info.Id) ?? _statuses.FirstOrDefault(), animate: false);
    }

    private void AddGroup(string title, IEnumerable<PlatformStatus> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        List.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(18, List.Children.Count == 0 ? 0 : 22, 0, 6),
        });
        foreach (var s in list) List.Children.Add(BuildRow(s));
    }

    private SlideHighlight? _highlight;

    // ---------------- État des lanceurs ----------------

    private readonly Dictionary<Platform, (Border Dot, TextBlock Text)> _rowStates = [];
    private HashSet<Platform> _running = [];

    private static readonly Color Running = Color.FromRgb(0x6B, 0xD3, 0x6B), Closed = Color.FromRgb(0xFF, 0x9F, 0x1C);

    /// <summary>État d'un lanceur installé (les lanceurs absents n'affichent rien).</summary>
    private (Color Color, string Text) StateOf(PlatformStatus s)
        => _running.Contains(s.Info.Id) ? (Running, "Application lancée") : (Closed, "Application fermée");

    private void ApplyState(PlatformStatus s, Border dot, TextBlock text)
    {
        var (color, label) = StateOf(s);
        dot.Background = new SolidColorBrush(color);
        text.Text = label;
    }

    /// <summary>Relit quels lanceurs sont ouverts (à l'affichage de l'onglet, puis régulièrement).</summary>
    public void RefreshRunning()
    {
        _running = _statuses.Where(s => s.ClientInstalled && s.Info.IsLauncherRunning).Select(s => s.Info.Id).ToHashSet();
        foreach (var s in _statuses)
            if (_rowStates.TryGetValue(s.Info.Id, out var row)) ApplyState(s, row.Dot, row.Text);
        if (_current is { } current) ApplyShowcaseState(current);
    }

    private void ApplyShowcaseState(PlatformStatus s)
    {
        StatusChip.Visibility = s.ClientInstalled || s.Info.Id == Platform.Manual ? Visibility.Visible : Visibility.Collapsed;
        var (color, label) = s.Info.Id == Platform.Manual ? (Color.FromRgb(0x6B, 0x74, 0x84), "Sans lanceur") : StateOf(s);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = label;
        StatusText.Foreground = (Brush)FindResource(s.ClientInstalled ? "TextPrimary" : "TextSecondary");
    }

    private Button BuildRow(PlatformStatus s)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Path
        {
            Data = Geometry.Parse(s.Info.Logo),
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(logo);

        // Nom, et en dessous l'état du lanceur installé (pastille + texte).
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = s.Info.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (s.Info.Id != Platform.Manual && s.ClientInstalled)
        {
            var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
            var text = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            state.Children.Add(dot);
            state.Children.Add(text);
            name.Children.Add(state);
            _rowStates[s.Info.Id] = (dot, text);
            ApplyState(s, dot, text);
        }
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var count = new TextBlock
        {
            Text = s.Owned > 0 ? s.Owned.ToString() : "",
            FontFamily = (FontFamily)FindResource("Display"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = (Brush)FindResource("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        var row = new Button
        {
            Content = grid,
            Tag = s,
            Style = (Style)FindResource("RowButton"),
            Opacity = s.ClientInstalled || s.Owned > 0 ? 1 : 0.55,
        };
        RowOptions.SetNoBar(row, true);
        RowOptions.SetFloating(row, true);
        row.GotKeyboardFocus += (_, _) =>
        {
            _highlight ??= new SlideHighlight(ListHighlight, ListHost);
            _highlight.MoveTo(row);
            Show(s, animate: true);
        };
        return row;
    }

    // ---------------- Vitrine ----------------

    private void Show(PlatformStatus? s, bool animate)
    {
        if (s is null) return;
        var changed = _current?.Info.Id != s.Info.Id;
        _current = s;
        var info = s.Info;
        var color = (Color)ColorConverter.ConvertFromString(info.Color);
        Color WithAlpha(byte a) => Color.FromArgb(a, color.R, color.G, color.B);

        BigLogo.Data = Geometry.Parse(info.Logo);
        Watermark.Data = BigLogo.Data;
        LogoGlow.Color = color;
        NameText.Text = info.Name;
        TaglineText.Text = info.Tagline;

        ApplyShowcaseState(s);
        StatusChip.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

        var mine = _games.Where(g => g.Platform == info.Id).ToList();
        InstalledCount.Text = s.Installed.ToString();
        OwnedCount.Text = s.Owned.ToString();
        HoursCount.Text = (mine.Sum(g => (long)(g.PlaytimeMinutes ?? 0)) / 60).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

        var ratio = s.Owned == 0 ? 0 : (double)s.Installed / s.Owned;
        RatioFill.Background = new SolidColorBrush(color);
        RatioFill.BeginAnimation(WidthProperty, new DoubleAnimation(RatioBar.Width * ratio, Anim.Ms(600)) { EasingFunction = Anim.Out });
        RatioText.Text = s.Owned == 0 ? "Aucun jeu dans la bibliothèque" : $"{ratio:P0} de la bibliothèque est installée";

        var preview = mine
            .OrderByDescending(g => g.LastPlayedAny ?? DateTime.MinValue)
            .ThenByDescending(g => g.IsInstalled)
            .ThenByDescending(g => g.CoverPath is not null)
            .ThenBy(g => g.DisplayTitle)
            .Take(8)
            .ToList();
        Covers.ItemsSource = preview;
        GamesLabel.Text = preview.Count > 0 ? "À LA UNE" : "JEUX";
        NoGamesText.Text = s.ClientInstalled
            ? "Aucun jeu détecté pour l'instant."
            : $"Installe le lanceur {info.Name} pour y retrouver tes jeux.";
        NoGamesText.Visibility = preview.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LauncherText.Text = s.ClientInstalled && info.LauncherUri is not null ? $"Ouvrir {info.Name}" : "Télécharger le lanceur";

        // Halo : la couleur glisse vers celle de la nouvelle plateforme.
        GlowStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithAlpha(0x5C), Anim.Ms(700)) { EasingFunction = Anim.Out });
        GlowEnd.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithAlpha(0x00), Anim.Ms(700)) { EasingFunction = Anim.Out });

        if (!animate || !changed) return;
        Anim.Enter(Showcase, 0, 18, 360);
        var drift = ((TransformGroup)Watermark.RenderTransform).Children.OfType<TranslateTransform>().First();
        drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, Anim.Ms(800)) { EasingFunction = Anim.Out });
        var scale = new ScaleTransform(0.8, 0.8);
        LogoBadge.RenderTransformOrigin = new Point(0.5, 0.5);
        LogoBadge.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, Anim.Ms(520)) { EasingFunction = Anim.Spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, Anim.Ms(520)) { EasingFunction = Anim.Spring });
    }

    private void Launcher_Click(object sender, RoutedEventArgs e) { if (_current is { } s) GameLauncher.OpenPlatform(s.Info, s.ClientInstalled); }

    // ---------------- Navigation ----------------

    public void FocusFirst()
    {
        if (List.IsKeyboardFocusWithin) return;
        var target = List.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as PlatformStatus)?.Info.Id == _current?.Info.Id)
                     ?? List.Children.OfType<Button>().FirstOrDefault();
        if (target is not null)
            Dispatcher.BeginInvoke(() => FocusNav.Focus(target), System.Windows.Threading.DispatcherPriority.Input);
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down:
                FocusNav.Move(action, List);
                return true;
            case NavAction.Accept:
                return true;
            case NavAction.Secondary:
                if (_current is { } s) GameLauncher.OpenPlatform(s.Info, s.ClientInstalled);
                return true;
        }
        return false;
    }
}
