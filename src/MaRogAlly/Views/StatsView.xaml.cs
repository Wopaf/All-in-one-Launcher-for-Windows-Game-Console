using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace MaRogAlly.Views;

/// <summary>
/// Statistiques de la bibliothèque : chiffres clés, répartition par plateforme, temps de jeu,
/// genres, années de sortie, notes, espace disque. Haut / bas font défiler, B ferme.
/// </summary>
public partial class StatsView : OverlayBase
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Taille des dossiers déjà mesurés pendant la session (le calcul peut être long).</summary>
    private static readonly ConcurrentDictionary<string, long> DiskCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Avancement du calcul d'espace disque (null = terminé).</summary>
    public event Action<string?, double?>? DiskProgress;

    private readonly List<Game> _games;
    private readonly List<FreeGame> _free;
    private readonly List<AchievementGame> _achievements;
    private readonly CancellationTokenSource _cts = new();
    private StackPanel? _diskList;

    public StatsView(IReadOnlyList<Game> games, IReadOnlyList<FreeGame> free, IReadOnlyList<AchievementGame> achievements)
    {
        InitializeComponent();
        _games = games.Where(g => !g.IsHidden).ToList();
        _free = free.ToList();
        _achievements = achievements.ToList();
        CloseRequested += _ => _cts.Cancel();
        Build();
    }

    protected override FrameworkElement AnimatedPart => Body;
    protected override double EnterFromX => 0;
    public override string Hints => _moreButton is { Visibility: Visibility.Visible } ? "↕:Défiler|X:Voir plus de jeux joués|B:Fermer" : "↕:Défiler|B:Fermer";

    public override void OnOpened()
    {
        base.OnOpened();
        _ = ComputeDiskAsync();
    }

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up:
                Anim.ScrollTo(Scroll, Scroll.VerticalOffset - 220, 260);
                return true;
            case NavAction.Down:
                Anim.ScrollTo(Scroll, Scroll.VerticalOffset + 220, 260);
                return true;
            case NavAction.Option:
                ShowMorePlayed();
                RaiseHintsChanged();
                return true;
            case NavAction.Back:
                Close();
                return true;
        }
        return true;
    }

    // =====================================================================
    // Construction
    // =====================================================================

    private void Build()
    {
        var played = _games.Where(g => g.PlaytimeMinutes > 0).ToList();
        var totalMinutes = played.Sum(g => g.PlaytimeMinutes!.Value);

        // ---- En-tête ----
        Body.Children.Add(new TextBlock { Text = "Statistiques", Style = Res<Style>("H1"), FontSize = 44 });
        Body.Children.Add(new TextBlock
        {
            Text = $"Ta bibliothèque au {DateTime.Now.ToString("d MMMM yyyy", Fr)} · jeux masqués exclus",
            Style = Res<Style>("BodyText"), Margin = new Thickness(0, 4, 0, 28),
        });

        // ---- Bandeau : temps de jeu total et jeu le plus joué ----
        var hero = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.Children.Add(TotalCard(played, totalMinutes));
        var spotlight = SpotlightCard(played.MaxBy(g => g.PlaytimeMinutes), totalMinutes);
        Grid.SetColumn(spotlight, 2);
        hero.Children.Add(spotlight);
        Body.Children.Add(hero);

        // ---- Sections en deux colonnes ----
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Body.Children.Add(columns);

        left.Children.Add(PlaytimeSection());
        left.Children.Add(DistributionSection());
        left.Children.Add(YearSection());
        left.Children.Add(DiskSection());

        right.Children.Add(AchievementSection());
        right.Children.Add(PlaytimeByGenreSection());
        right.Children.Add(ActivitySection());
        right.Children.Add(PlatformSection());
    }

    /// <summary>Carte principale : temps de jeu total en très grand, puis quatre chiffres clés.</summary>
    private Border TotalCard(List<Game> played, int totalMinutes)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "TEMPS DE JEU TOTAL", Style = Res<Style>("Label") });
        panel.Children.Add(new TextBlock
        {
            Text = totalMinutes > 0 ? Hours(totalMinutes) : "–",
            FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.Bold, FontSize = 68, Foreground = Res<Brush>("TextPrimary"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = totalMinutes >= 1440 ? $"soit {totalMinutes / 1440.0:0.#} jours de jeu, sur {played.Count} jeux joués" : $"sur {played.Count} jeux joués",
            FontSize = 15, Foreground = Res<Brush>("TextSecondary"),
        });

        var recent = _games.Count(g => g.LastPlayedAny is { } d && DateTime.Now - d < TimeSpan.FromDays(30));
        var platinum = _achievements.Count(a => a.Completed);
        var kpis = new UniformGrid { Columns = 4, Margin = new Thickness(0, 28, 0, 0) };
        kpis.Children.Add(Kpi(_games.Count.ToString("N0", Fr), "JEUX"));
        kpis.Children.Add(Kpi(recent.ToString("N0", Fr), "JOUÉS CE MOIS-CI"));
        kpis.Children.Add(Kpi(played.Count > 0 ? Hours(totalMinutes / played.Count) : "–", "PAR JEU JOUÉ"));
        kpis.Children.Add(Kpi(_achievements.Sum(a => a.Unlocked).ToString("N0", Fr), platinum > 0 ? $"SUCCÈS · {platinum} PLATINE" : "SUCCÈS"));
        panel.Children.Add(kpis);
        return Card(panel, new Thickness(32, 28, 32, 28));
    }

    private StackPanel Kpi(string value, string label)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(new TextBlock { Text = value, FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.Bold, FontSize = 26, Foreground = Res<Brush>("TextPrimary") });
        sp.Children.Add(new TextBlock { Text = label, Style = Res<Style>("Label"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    /// <summary>Le jeu le plus joué mis en avant : son visuel en fond, sa jaquette, son temps de jeu.</summary>
    private FrameworkElement SpotlightCard(Game? top, int totalMinutes)
    {
        var card = new Grid { MinHeight = 250, Background = Res<Brush>("SurfaceGlass") };
        Rounded.SetRadius(card, 22);
        if (top is null)
        {
            card.Children.Add(new Border { Padding = new Thickness(32, 28, 32, 28), Child = Empty("Le jeu le plus joué apparaîtra ici dès qu'un temps de jeu sera connu.") });
            return card;
        }

        var bg = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.9 };
        ImageLoader.SetDecodeWidth(bg, 1200);
        ImageLoader.SetSource(bg, top.HeroPath ?? top.CoverPath);
        card.Children.Add(bg);
        card.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0xF2, 0x0A, 0x0C, 0x11), Color.FromArgb(0x40, 0x0A, 0x0C, 0x11), new Point(0, 0.5), new Point(1, 0.5)),
        });

        var content = new Grid { Margin = new Thickness(30, 26, 30, 26), VerticalAlignment = VerticalAlignment.Bottom };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        var cover = new Grid { Width = 110, Height = 165, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2A)), Margin = new Thickness(0, 0, 24, 0) };
        Rounded.SetRadius(cover, 12);
        var coverImg = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(coverImg, 240);
        ImageLoader.SetSource(coverImg, top.CoverPath);
        cover.Children.Add(coverImg);
        content.Children.Add(cover);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(new TextBlock { Text = "TON JEU LE PLUS JOUÉ", Style = Res<Style>("Label") });
        text.Children.Add(new TextBlock { Text = top.DisplayTitle, Style = Res<Style>("H2"), FontSize = 30, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        text.Children.Add(new TextBlock
        {
            Text = $"{Hours(top.PlaytimeMinutes!.Value)} · {Percent(top.PlaytimeMinutes!.Value, totalMinutes)} de ton temps de jeu",
            FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.SemiBold, FontSize = 17, Foreground = Res<Brush>("TextPrimary"), Margin = new Thickness(0, 10, 0, 0),
        });
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        info.Children.Add(new Path { Data = Geometry.Parse(top.PlatformInfo.Logo), Fill = Res<Brush>("TextSecondary"), Stretch = Stretch.Uniform, Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        info.Children.Add(new TextBlock
        {
            Text = top.LastPlayedAny is { } last ? $"{top.PlatformInfo.Name} · dernière partie {Ago(last)}" : top.PlatformInfo.Name,
            FontSize = 13, Foreground = Res<Brush>("TextSecondary"),
        });
        text.Children.Add(info);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        card.Children.Add(content);
        return card;
    }

    /// <summary>Fond de carte sans contour.</summary>
    private Border Card(UIElement child, Thickness padding) => new()
    {
        CornerRadius = new CornerRadius(22),
        Background = Res<Brush>("SurfaceGlass"),
        Padding = padding,
        Child = child,
    };

    // ---- Les plus joués (10 par 10 avec « Voir plus ») ----

    private StackPanel? _topList;
    private Button? _moreButton;
    private int _topShown = 10;

    private Border PlaytimeSection()
    {
        var all = _games.Where(g => g.PlaytimeMinutes > 0).OrderByDescending(g => g.PlaytimeMinutes).ToList();
        if (all.Count == 0)
            return Section("Les plus joués", null, Empty("Aucun temps de jeu connu pour l'instant. Steam et GOG Galaxy le fournissent automatiquement ; la clé API Steam l'ajoute aussi."));

        var total = all.Sum(g => g.PlaytimeMinutes!.Value);
        var panel = new StackPanel();
        _topList = new StackPanel();
        panel.Children.Add(_topList);
        _moreButton = new Button
        {
            // X le déclenche à la manette : pas de focus, sinon la page s'ouvrirait défilée jusqu'à lui.
            Focusable = false,
            Style = Res<Style>("PillButton"), Height = 36, Padding = new Thickness(16, 0, 16, 0), FontSize = 14,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
        };
        _moreButton.Click += (_, _) => ShowMorePlayed();
        panel.Children.Add(_moreButton);

        void Fill()
        {
            _topList.Children.Clear();
            var top = all.Take(_topShown).ToList();
            _topList.Children.Add(BarList(top.Select((g, i) => ($"{i + 1}. {g.DisplayTitle}", (double)g.PlaytimeMinutes!.Value,
                $"{Hours(g.PlaytimeMinutes!.Value)} · {Percent(g.PlaytimeMinutes!.Value, total)}", (Platform?)g.Platform)), maxOverride: all[0].PlaytimeMinutes!.Value));
            var left = all.Count - top.Count;
            _moreButton.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
            _moreButton.Content = $"Voir plus ({left} autre{(left > 1 ? "s" : "")})";
        }
        _fillTop = Fill;
        Fill();
        return Section("Les plus joués", $"Temps de jeu par titre · {all.Count} jeux joués", panel);
    }

    private Action? _fillTop;

    private void ShowMorePlayed()
    {
        if (_moreButton is not { Visibility: Visibility.Visible }) return;
        _topShown += 10;
        _fillTop?.Invoke();
    }

    // ---- Temps de jeu par plateforme et par genre ----

    private List<(string? Genre, int Minutes)> PlaytimeByGenre()
        => _games.Where(g => g.PlaytimeMinutes > 0)
            .SelectMany(g => (g.Meta?.Genres ?? []).Select(genre => (Genre: (string?)genre, Minutes: g.PlaytimeMinutes!.Value)))
            .GroupBy(x => x.Genre).Select(x => (x.Key, x.Sum(v => v.Minutes)))
            .OrderByDescending(x => x.Item2).ToList();

    private Border PlaytimeByGenreSection()
    {
        var genres = PlaytimeByGenre().Take(8).ToList();
        if (genres.Count == 0) return Section("Temps de jeu par genre", null, Empty("Les genres apparaîtront une fois les fiches téléchargées."));
        var counts = _games.SelectMany(g => g.Meta?.Genres ?? []).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        return Section("Temps de jeu par genre", "Un jeu compte dans chacun de ses genres",
            BarList(genres.Select(g => ($"{g.Genre} · {counts.GetValueOrDefault(g.Genre!)} jeux", (double)g.Minutes, Hours(g.Minutes), (Platform?)null))));
    }

    // ---- Répartition des temps de jeu ----

    private Border DistributionSection()
    {
        var buckets = new (string Label, Func<int, bool> Match)[]
        {
            ("Moins d'1 h", m => m is > 0 and < 60),
            ("1 à 10 h", m => m is >= 60 and < 600),
            ("10 à 50 h", m => m is >= 600 and < 3000),
            ("50 à 100 h", m => m is >= 3000 and < 6000),
            ("Plus de 100 h", m => m >= 6000),
        };
        var played = _games.Count(g => g.PlaytimeMinutes > 0);
        if (played == 0) return Section("Répartition des temps de jeu", null, Empty("Aucun temps de jeu connu pour l'instant."));
        var rows = buckets.Select(b => (b.Label, Count: _games.Count(g => b.Match(g.PlaytimeMinutes ?? 0)))).ToList();
        return Section("Répartition des temps de jeu", "Jeux joués, selon le temps passé dessus",
            BarList(rows.Select(r => (r.Label, (double)r.Count, $"{r.Count} · {Percent(r.Count, played)}", (Platform?)null))));
    }

    // ---- Succès ----

    private Border AchievementSection()
    {
        var detailed = _achievements.Where(a => a.Total > 0).ToList();
        if (detailed.Count == 0)
            return Section("Succès", null, Empty("Aucun succès connu pour l'instant : ils apparaissent dans l'onglet Succès une fois récupérés."));

        var total = detailed.Sum(a => a.Total);
        var unlocked = detailed.Sum(a => a.Unlocked);
        var items = detailed.SelectMany(a => a.Items.Select(i => (Game: a, Item: i))).ToList();
        var panel = new StackPanel();

        // Taux de complétion global, en grand avec sa barre.
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new TextBlock { Text = Percent(unlocked, total), FontFamily = Res<FontFamily>("Display"), FontWeight = FontWeights.Bold, FontSize = 34, Foreground = Res<Brush>("TextPrimary"), Margin = new Thickness(0, 0, 16, 0) });
        var sub = new TextBlock { Text = $"de tous les succès débloqués\n{unlocked:N0} sur {total:N0} · {detailed.Count} jeux", FontSize = 13, Foreground = Res<Brush>("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sub, 1);
        head.Children.Add(sub);
        panel.Children.Add(head);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 18) };
        var scale = new BarScale(Math.Max(1, total));
        bar.Children.Add(scale.Bar(unlocked, Res<Brush>("Accent")));
        panel.Children.Add(bar);
        scale.BindTo(bar, panel);

        panel.Children.Add(KeyValue("Jeux platine (100 %)", detailed.Count(a => a.Completed).ToString(), null));
        panel.Children.Add(KeyValue("Jeux commencés", detailed.Count(a => a.Unlocked > 0).ToString(), null));
        panel.Children.Add(KeyValue("Succès rares débloqués (moins de 10 % des joueurs)", items.Count(x => x.Item.Unlocked && x.Item.Rarity < 10).ToString(), null));
        if (items.Where(x => x.Item.Unlocked && x.Item.Rarity is not null).MinBy(x => x.Item.Rarity) is { Item: { } rarest } r)
            panel.Children.Add(KeyValue($"Le plus rare : {rarest.Name}", $"{r.Game.DisplayTitle} · {rarest.Rarity!.Value.ToString("0.#", Fr)} %", r.Game.Platform));

        var close = detailed.Where(a => !a.Completed && a.Unlocked > 0).OrderByDescending(a => a.Ratio).Take(5).ToList();
        if (close.Count > 0)
        {
            panel.Children.Add(SubTitle("Presque terminés"));
            panel.Children.Add(BarList(close.Select(a => (a.DisplayTitle, a.Ratio * 100, $"{a.Unlocked} / {a.Total} · {a.Ratio:P0}", (Platform?)a.Platform)), maxOverride: 100));
        }

        var last = items.Where(x => x.Item.Unlocked && x.Item.UnlockedAt is not null).OrderByDescending(x => x.Item.UnlockedAt).Take(5).ToList();
        if (last.Count > 0)
        {
            panel.Children.Add(SubTitle("Derniers succès débloqués"));
            foreach (var (game, item) in last)
                panel.Children.Add(KeyValue($"{item.Name} · {game.DisplayTitle}", item.UnlockedAt!.Value.ToString("d MMM yyyy", Fr), game.Platform));
        }
        return Section("Succès", "Toutes plateformes réunies", panel);
    }

    // ---- Activité récente ----

    private Border ActivitySection()
    {
        var recent = _games.Where(g => g.LastPlayedAny is not null).OrderByDescending(g => g.LastPlayedAny).Take(6).ToList();
        if (recent.Count == 0) return Section("Activité récente", null, Empty("Aucune partie connue pour l'instant."));
        var list = new StackPanel();
        foreach (var g in recent) list.Children.Add(KeyValue(g.DisplayTitle, Ago(g.LastPlayedAny!.Value), g.Platform));
        var week = _games.Count(g => g.LastPlayedAny is { } d && DateTime.Now - d < TimeSpan.FromDays(7));
        return Section("Activité récente", $"{week} jeu{(week > 1 ? "x" : "")} lancé{(week > 1 ? "s" : "")} ces 7 derniers jours", list);
    }

    private static string Ago(DateTime d)
    {
        var days = (DateTime.Today - d.Date).Days;
        return days switch
        {
            <= 0 => $"aujourd'hui, {d:HH:mm}",
            1 => "hier",
            < 30 => $"il y a {days} jours",
            _ => d.ToString("d MMM yyyy", Fr),
        };
    }

    private TextBlock SubTitle(string text)
        => new() { Text = text.ToUpperInvariant(), Style = Res<Style>("Label"), Margin = new Thickness(0, 18, 0, 10) };

    private Border PlatformSection()
    {
        var groups = _games.GroupBy(g => g.Platform)
            .Select(g => (Info: PlatformInfo.Of(g.Key), Owned: g.Count(), Installed: g.Count(x => x.IsInstalled)))
            .OrderByDescending(x => x.Owned).ToList();
        var max = Math.Max(1, groups.Select(g => g.Owned).DefaultIfEmpty(1).Max());

        var list = new StackPanel();
        // Légende : deux séries (installés / bibliothèque), la couleur n'est jamais seule à porter le sens.
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        legend.Children.Add(LegendItem(Res<Brush>("Accent"), "Installés"));
        legend.Children.Add(LegendItem(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)), "Non installés"));
        list.Children.Add(legend);

        foreach (var (info, owned, inst) in groups)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new Path { Data = Geometry.Parse(info.Logo), Fill = Brushes.White, Stretch = Stretch.Uniform, Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0) });
            name.Children.Add(new TextBlock { Text = info.Name, FontSize = 14, Foreground = Res<Brush>("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(name);

            var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var scale = new BarScale(max);
            if (inst > 0) bars.Children.Add(scale.Bar(inst, Res<Brush>("Accent")));
            if (owned - inst > 0) bars.Children.Add(scale.Bar(owned - inst, new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)), gapBefore: inst > 0));
            Grid.SetColumn(bars, 1);
            row.Children.Add(bars);
            scale.BindTo(bars, row);

            var value = new TextBlock
            {
                Text = $"{inst} / {owned}", FontFamily = Res<FontFamily>("Display"), FontSize = 14,
                Foreground = Res<Brush>("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            list.Children.Add(row);
        }
        return Section("Par plateforme", "Jeux installés sur le total de chaque bibliothèque", list);
    }

    private Border YearSection()
    {
        var years = _games.Where(g => g.ReleaseYear is not null).Select(g => g.ReleaseYear!.Value).ToList();
        if (years.Count == 0) return Section("Années de sortie", null, Empty("Les dates de sortie apparaîtront une fois les fiches téléchargées."));

        // Une colonne par année si la période est courte, sinon par tranches de 5 ans.
        var min = years.Min();
        var max = years.Max();
        var step = max - min > 24 ? 5 : 1;
        var start = min / step * step;
        var buckets = new List<(string Label, int Count)>();
        for (var y = start; y <= max; y += step)
            buckets.Add((step == 1 ? y.ToString() : $"{y}–{y + step - 1}", years.Count(v => v >= y && v < y + step)));

        var peak = Math.Max(1, buckets.Max(b => b.Count));
        var chart = new Grid { Height = 170 };
        var labelEvery = Math.Max(1, (int)Math.Ceiling(buckets.Count / 8.0));
        for (var i = 0; i < buckets.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            var (label, count) = buckets[i];
            var col = new Grid { Margin = new Thickness(1, 0, 1, 0) };
            col.RowDefinitions.Add(new RowDefinition());
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            var bar = new Border
            {
                Background = Res<Brush>("Accent"),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 0,
                ToolTip = $"{label} : {count} jeu{(count > 1 ? "x" : "")}",
                Opacity = count == 0 ? 0 : 1,
            };
            var target = 140.0 * count / peak;
            bar.Loaded += (_, _) => bar.BeginAnimation(HeightProperty, new DoubleAnimation(0, target, Anim.Ms(700)) { EasingFunction = Anim.Out, BeginTime = TimeSpan.FromMilliseconds(20 * i) });
            col.Children.Add(bar);
            if (i % labelEvery == 0 || i == buckets.Count - 1)
            {
                var text = new TextBlock { Text = label, FontSize = 11, Foreground = Res<Brush>("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(text, 1);
                col.Children.Add(text);
            }
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }

        var peakBucket = buckets.OrderByDescending(b => b.Count).First();
        return Section("Années de sortie", $"Pic : {peakBucket.Label} ({peakBucket.Count} jeux) · de {min} à {max}", chart);
    }

    private Border DiskSection()
    {
        _diskList = new StackPanel();
        _diskList.Children.Add(Empty("Calcul en cours…"));
        return Section("Espace disque", "Les jeux installés les plus volumineux", _diskList, out _diskSubtitle);
    }

    // =====================================================================
    // Espace disque (calcul en tâche de fond)
    // =====================================================================

    private async Task ComputeDiskAsync()
    {
        var targets = _games.Where(g => g.IsInstalled && g.InstallDir is { } d && Directory.Exists(d))
            .GroupBy(g => g.InstallDir!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var sizes = new List<(Game Game, long Bytes)>();
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var g = targets[i];
                if (!DiskCache.TryGetValue(g.InstallDir!, out var bytes))
                {
                    DiskProgress?.Invoke($"{g.DisplayTitle} · {i + 1} sur {targets.Count}", (double)i / targets.Count);
                    var dir = g.InstallDir!;
                    bytes = await Task.Run(() => FolderSize(dir, _cts.Token), _cts.Token);
                    DiskCache[dir] = bytes;
                }
                sizes.Add((g, bytes));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            DiskProgress?.Invoke(null, null);
        }

        var total = sizes.Sum(s => s.Bytes);
        if (_diskList is null) return;
        _diskList.Children.Clear();
        var top = sizes.Where(s => s.Bytes > 0).OrderByDescending(s => s.Bytes).Take(8).ToList();
        if (top.Count == 0)
        {
            _diskList.Children.Add(Empty("Aucun dossier d'installation accessible."));
            return;
        }
        _diskList.Children.Add(BarList(top.Select(s => (s.Game.DisplayTitle, (double)s.Bytes, Size(s.Bytes), (Platform?)s.Game.Platform))));
        if (_diskSubtitle is not null) _diskSubtitle.Text = $"Les jeux installés les plus volumineux · {Size(total)} au total";
    }

    private static long FolderSize(string dir, CancellationToken ct)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            try { total += f.Length; } catch { }
        }
        return total;
    }

    // =====================================================================
    // Briques visuelles
    // =====================================================================

    private TextBlock? _diskSubtitle;

    private Border Section(string title, string? subtitle, UIElement content) => Section(title, subtitle, content, out _);

    private Border Section(string title, string? subtitle, UIElement content, out TextBlock? subtitleText)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2"), FontSize = 21 });
        subtitleText = null;
        if (subtitle is not null)
            panel.Children.Add(subtitleText = new TextBlock { Text = subtitle, FontSize = 13, Foreground = Res<Brush>("TextMuted"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        var holder = new Border { Margin = new Thickness(0, 18, 0, 0), Child = content };
        panel.Children.Add(holder);
        var card = Card(panel, new Thickness(28, 24, 28, 24));
        card.Margin = new Thickness(0, 0, 0, 24);
        return card;
    }

    /// <summary>Liste de barres horizontales (une seule série, une seule couleur) avec valeur écrite.</summary>
    private StackPanel BarList(IEnumerable<(string Label, double Value, string Text, Platform? Platform)> items, double? maxOverride = null)
    {
        var list = items.ToList();
        var scale = new BarScale(maxOverride ?? Math.Max(1, list.Max(i => i.Value)));
        var panel = new StackPanel();
        foreach (var (label, v, text, platform) in list)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new StackPanel { Orientation = Orientation.Horizontal };
            if (platform is { } p)
                name.Children.Add(new Path { Data = Geometry.Parse(PlatformInfo.Of(p).Logo), Fill = Res<Brush>("TextMuted"), Stretch = Stretch.Uniform, Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = Res<Brush>("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(name);

            var value = new TextBlock { Text = text, FontFamily = Res<FontFamily>("Display"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("TextSecondary"), Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            var bars = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            bars.Children.Add(scale.Bar(v, Res<Brush>("Accent")));
            Grid.SetRow(bars, 1);
            Grid.SetColumnSpan(bars, 2);
            row.Children.Add(bars);
            scale.BindTo(bars, row);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Grid KeyValue(string key, string value, Platform? platform)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        if (platform is { } p)
            name.Children.Add(new Path { Data = Geometry.Parse(PlatformInfo.Of(p).Logo), Fill = Res<Brush>("TextMuted"), Stretch = Stretch.Uniform, Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = key, FontSize = 14, Foreground = Res<Brush>("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(name);
        var v = new TextBlock { Text = value, FontSize = 14, Foreground = Res<Brush>("TextSecondary"), Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(v, 1);
        row.Children.Add(v);
        return row;
    }

    private StackPanel LegendItem(Brush color, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 0) };
        sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = color, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Res<Brush>("TextSecondary") });
        return sp;
    }

    private TextBlock Empty(string text)
        => new() { Text = text, FontSize = 14, Foreground = Res<Brush>("TextMuted"), TextWrapping = TextWrapping.Wrap };

    private T Res<T>(string key) => (T)FindResource(key);

    private static string Percent(int part, int total) => total == 0 ? "–" : $"{(double)part / total:P0}";

    private static string Hours(int minutes) => minutes >= 600 ? $"{minutes / 60:N0} h".Replace(' ', ' ') : Game.FormatDuration(minutes);

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };

    /// <summary>
    /// Barres proportionnelles à une valeur maximale. Les largeurs sont calculées quand la place
    /// disponible est connue, puis animées depuis zéro.
    /// </summary>
    private sealed class BarScale(double max)
    {
        private readonly List<(Border Bar, double Value, bool Gap)> _bars = [];

        public Border Bar(double value, Brush brush, bool gapBefore = false)
        {
            // Extrémités arrondies de 4 px ; 2 px d'écart entre deux segments voisins.
            var b = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = brush, Width = 0, Margin = new Thickness(gapBefore ? 2 : 0, 0, 0, 0) };
            _bars.Add((b, value, gapBefore));
            return b;
        }

        public void BindTo(Panel bars, FrameworkElement host)
        {
            void Layout()
            {
                var width = bars.ActualWidth > 0 ? bars.ActualWidth : (bars.Parent as FrameworkElement)?.ActualWidth ?? 0;
                if (width <= 0) return;
                foreach (var (bar, value, gap) in _bars)
                {
                    var w = Math.Max(value > 0 ? 4 : 0, width * value / max - (gap ? 2 : 0));
                    bar.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, Anim.Ms(700)) { EasingFunction = Anim.Out });
                }
            }
            host.SizeChanged += (_, _) => Layout();
        }
    }
}
