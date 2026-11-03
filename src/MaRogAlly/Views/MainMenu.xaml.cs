using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Tuile du menu principal : icône, titre et une ligne de détail (avec pastille d'état éventuelle).</summary>
public sealed record MenuTile(string Icon, string Title, string Detail, Action OnClick, Color? Dot = null);

/// <summary>
/// Menu principal (Start ou Échap) : heure, date et batterie en en-tête, puis des tuiles par section
/// (mises à jour, accès rapides, alimentation). Le cadre de sélection glisse comme dans les Réglages.
/// </summary>
public partial class MainMenu : OverlayBase
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private readonly SlideHighlight _highlight;

    public MainMenu()
    {
        InitializeComponent();
        _highlight = new SlideHighlight(Highlight, ItemsHost);
        UpdateHeader();
    }

    protected override FrameworkElement AnimatedPart => Panel;
    protected override FrameworkElement FocusScope => Items;
    public override string Hints => _brightnessRow?.IsKeyboardFocused == true ? "←→:Luminosité|B:Fermer" : "A:Valider|B:Fermer";

    private void UpdateHeader()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        var date = now.ToString("dddd d MMMM", Fr);
        DateText.Text = char.ToUpper(date[0]) + date[1..];

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

    public void AddSection(string title)
        => Items.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(4, 22, 0, 10),
        });

    /// <summary>Rangée de tuiles de même largeur (2 par rangée en général).</summary>
    public void AddTiles(params MenuTile[] tiles)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < tiles.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var b = Tile(tiles[i]);
            b.Margin = new Thickness(i == 0 ? 0 : 6, 0, i == tiles.Length - 1 ? 0 : 6, 0);
            Grid.SetColumn(b, i);
            row.Children.Add(b);
        }
        Items.Children.Add(row);
    }

    /// <summary>Bouton simple en pleine largeur : icône, libellé, chevron.</summary>
    public void AddButton(string icon, string title, Action onClick)
    {
        var b = MenuButton(icon, title, onClick);
        b.Margin = new Thickness(0, 0, 0, 8);
        Items.Children.Add(b);
    }

    /// <summary>Boutons simples côte à côte, de même largeur.</summary>
    public void AddButtons(params (string Icon, string Title, Action OnClick)[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var b = MenuButton(buttons[i].Icon, buttons[i].Title, buttons[i].OnClick);
            b.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0);
            Grid.SetColumn(b, i);
            row.Children.Add(b);
        }
        Items.Children.Add(row);
    }

    private Button MenuButton(string icon, string title, Action onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var b = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Padding = new Thickness(0),
            Height = 56,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 0, 20, 0),
                // Le modèle de ligne centre son contenu : le fond prend toute la hauteur du bouton.
                Height = 56,
                Child = grid,
            },
        };
        Track(b, onClick);
        return b;
    }

    /// <summary>Rangée de boutons de même largeur, avec leur nom dessous.</summary>
    public void AddIconButtons(params (string Icon, string Title, Action OnClick)[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var (icon, title, onClick) = buttons[i];
            var tile = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(14),
                Height = 56,
                Child = new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var b = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Padding = new Thickness(0),
                Height = 80,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0),
                Content = new StackPanel
                {
                    Children =
                    {
                        tile,
                        new TextBlock { Text = title, FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) },
                    },
                },
            };
            _iconTiles[title] = tile;
            // Le cadre de sélection entoure la tuile, pas le nom dessous.
            Track(b, onClick, tile);
            Grid.SetColumn(b, i);
            row.Children.Add(b);
        }
        Items.Children.Add(row);
    }

    private readonly Dictionary<string, Border> _iconTiles = [];

    /// <summary>Fonction activée (Wi-Fi, Bluetooth) : la tuile passe en dégradé bleu.</summary>
    public void SetIconActive(string title, bool active)
    {
        if (!_iconTiles.TryGetValue(title, out var tile)) return;
        tile.BorderThickness = new Thickness(1);
        if (!active)
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            tile.BorderBrush = Brushes.Transparent;
            return;
        }
        static Color Blue(byte alpha) => Color.FromArgb(alpha, 0x2D, 0x8C, 0xFF);
        tile.Background = new LinearGradientBrush(new GradientStopCollection { new(Blue(0x8C), 0), new(Blue(0x33), 1) }, new Point(0, 0), new Point(1, 1));
        tile.BorderBrush = new LinearGradientBrush(Blue(0xB3), Blue(0x26), new Point(0, 0), new Point(1, 1));
    }

    private const int BrightnessMin = 10, BrightnessStep = 10;
    private Button? _brightnessRow;
    private Border? _brightnessFill;
    private FrameworkElement? _brightnessBar;
    private TextBlock? _brightnessText;
    private Action<int>? _applyBrightness;
    private int _brightness;

    /// <summary>Curseur de luminosité : gauche et droite à la manette, ou glissement au doigt et à la souris. Jamais tout à fait noir.</summary>
    public void AddBrightness(int percent, Action<int> apply)
    {
        _applyBrightness = apply;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.Children.Add(new TextBlock { Text = "\uE706", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        // Fond transparent : toute la hauteur de la barre répond au doigt.
        var bar = new Grid { Background = Brushes.Transparent, Height = 32 };
        bar.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        _brightnessFill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Accent") };
        bar.Children.Add(_brightnessFill);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        _brightnessText = new TextBlock { FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_brightnessText, 2);
        grid.Children.Add(_brightnessText);
        _brightnessBar = bar;
        bar.SizeChanged += (_, _) => ShowBrightness(_brightness, animate: false);

        _brightnessRow = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Padding = new Thickness(0),
            Height = 56,
            ToolTip = "Luminosité",
            Margin = new Thickness(0, 0, 0, 8),
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 0, 20, 0),
                Height = 56,
                Child = grid,
            },
        };
        Track(_brightnessRow, () => { });
        _brightnessRow.GotKeyboardFocus += (_, _) => RaiseHintsChanged();
        _brightnessRow.LostKeyboardFocus += (_, _) => RaiseHintsChanged();
        bar.MouseLeftButtonDown += (_, e) =>
        {
            FocusNav.Focus(_brightnessRow);
            bar.CaptureMouse();
            DragBrightness(e.GetPosition(bar).X);
            e.Handled = true;
        };
        bar.MouseMove += (_, e) => { if (bar.IsMouseCaptured) DragBrightness(e.GetPosition(bar).X); };
        bar.MouseLeftButtonUp += (_, e) =>
        {
            bar.ReleaseMouseCapture();
            e.Handled = true;
        };
        Items.Children.Add(_brightnessRow);
        ShowBrightness(percent, animate: false);
    }

    private void DragBrightness(double x)
    {
        if (_brightnessBar is { ActualWidth: > 0 } bar) ChangeBrightness((int)Math.Round(x / bar.ActualWidth * 100 / BrightnessStep) * BrightnessStep);
    }

    private void ChangeBrightness(int percent)
    {
        percent = Math.Clamp(percent, BrightnessMin, 100);
        if (percent == _brightness) return;
        ShowBrightness(percent);
        _applyBrightness?.Invoke(percent);
    }

    /// <summary>Affiche un niveau, arrondi à la dizaine, sans le modifier (ex. changé entre-temps par les touches de la machine).</summary>
    public void ShowBrightness(int percent, bool animate = true)
    {
        if (_brightnessFill is null || _brightnessBar is null || _brightnessText is null) return;
        percent = Math.Clamp((int)Math.Round(percent / (double)BrightnessStep, MidpointRounding.AwayFromZero) * BrightnessStep, BrightnessMin, 100);
        _brightness = percent;
        _brightnessText.Text = $"{percent} %";
        var width = _brightnessBar.ActualWidth * percent / 100;
        if (animate) _brightnessFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, Anim.Ms(220)) { EasingFunction = Anim.Out });
        else
        {
            _brightnessFill.BeginAnimation(WidthProperty, null);
            _brightnessFill.Width = width;
        }
    }

    /// <summary>Boutons d'alimentation : pastille colorée avec l'icône, libellé à côté, côte à côte.</summary>
    public void AddPowerButtons(params (string Icon, string Title, Color Tint, Action OnClick)[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var (icon, title, tint, onClick) = buttons[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });

            var b = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Padding = new Thickness(0),
                Height = 68,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == buttons.Length - 1 ? 0 : 6, 0),
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B)),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(14, 0, 14, 0),
                    Height = 68,
                    Child = content,
                },
            };
            Track(b, onClick);
            Grid.SetColumn(b, i);
            row.Children.Add(b);
        }
        Items.Children.Add(row);
    }

    /// <summary>Disques de la machine : espace libre sur le total, barre d'occupation (orange sous 10 % libres).</summary>
    public void AddDrives()
    {
        var drives = System.IO.DriveInfo.GetDrives().Where(d => d.DriveType == System.IO.DriveType.Fixed && d.IsReady).ToList();
        if (drives.Count == 0) return;
        AddSection("Stockage");
        foreach (var d in drives)
        {
            long total, free;
            string label;
            try { total = d.TotalSize; free = d.AvailableFreeSpace; label = d.VolumeLabel; }
            catch { continue; }
            var used = total == 0 ? 0 : 1 - (double)free / total;
            var low = total > 0 && (double)free / total < 0.1;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = "\uEDA2", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            name.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(label) ? d.Name.TrimEnd('\\') : $"{d.Name.TrimEnd('\\')}  {label}",
                FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(name);
            var info = new TextBlock
            {
                Text = $"{Size(free)} libres sur {Size(total)}", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = low ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)) : (Brush)FindResource("TextSecondary"),
            };
            Grid.SetColumn(info, 1);
            head.Children.Add(info);

            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                Background = low ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)) : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            };
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * used;
            bar.Children.Add(fill);

            var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 14) };
            panel.Children.Add(head);
            panel.Children.Add(bar);
            Items.Children.Add(panel);
        }
    }

    private static string Size(long bytes) => bytes >= 1L << 40
        ? $"{bytes / (double)(1L << 40):0.##} To"
        : $"{bytes / (double)(1L << 30):0} Go";

    private Button? _lastButton;

    private void Track(Button b, Action onClick, FrameworkElement? frame = null)
    {
        RowOptions.SetFloating(b, true);
        b.GotKeyboardFocus += (_, _) =>
        {
            _lastButton = b;
            _highlight.MoveTo(frame ?? b);
        };
        b.Click += (_, _) => onClick();
    }

    /// <summary>Retour d'un écran ouvert par-dessus (Wi-Fi, Bluetooth) : la sélection reprend sur son bouton.</summary>
    public override void OnRevealed()
    {
        if (_lastButton is { } b) FocusNav.Focus(b);
    }

    /// <summary>Pastille et ligne d'état de chaque tuile, pour les mettre à jour menu ouvert.</summary>
    private readonly Dictionary<string, (Border Dot, TextBlock Text, Border Card)> _tileStates = [];

    /// <summary>Nouvel état d'une tuile (ex. fin de la vérification des mises à jour).</summary>
    public void UpdateTile(string title, string detail, Color dot)
    {
        if (!_tileStates.TryGetValue(title, out var state)) return;
        state.Text.Text = detail;
        state.Dot.Background = new SolidColorBrush(dot);
        state.Dot.Visibility = Visibility.Visible;
        ApplyTileLook(state.Card, dot);
    }

    /// <summary>Mise à jour à faire : voile orange qui part du coin de l'icône et s'efface en diagonale, avec un liseré assorti.</summary>
    private static void ApplyTileLook(Border card, Color? dot)
    {
        var alert = dot is { } c && c != Ok && c != Idle;
        card.BorderThickness = new Thickness(1);
        if (!alert)
        {
            card.Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            card.BorderBrush = Brushes.Transparent;
            return;
        }
        static Color Orange(byte alpha) => Color.FromArgb(alpha, 0xFF, 0x8A, 0x1C);
        card.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Orange(0x4D), 0), new(Orange(0x1F), 0.55), new(Orange(0x0A), 1),
        }, new Point(0, 0), new Point(1, 1));
        card.BorderBrush = new LinearGradientBrush(Orange(0x73), Orange(0x14), new Point(0, 0), new Point(1, 1));
    }

    private Button Tile(MenuTile t)
    {
        var detail = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detail.ColumnDefinitions.Add(new ColumnDefinition());
        var dotBorder = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(t.Dot ?? Colors.Transparent),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = t.Dot is null ? Visibility.Collapsed : Visibility.Visible,
        };
        detail.Children.Add(dotBorder);
        var detailText = new TextBlock
        {
            Text = t.Detail, FontSize = 13, Foreground = (Brush)FindResource("TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detailText, 1);
        detail.Children.Add(detailText);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(new TextBlock
        {
            Text = t.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 22,
            Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        text.Children.Add(new TextBlock
        {
            Text = t.Title, FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 17,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(detail);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 0, 12, 0),
            Height = 68,
            Child = body,
        };
        ApplyTileLook(card, t.Dot);

        var b = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Padding = new Thickness(0),
            Height = 68,
            Content = card,
        };
        _tileStates[t.Title] = (dotBorder, detailText, card);
        RowOptions.SetFloating(b, true);
        b.GotKeyboardFocus += (_, _) => _highlight.MoveTo(b);
        b.Click += (_, _) => t.OnClick();
        return b;
    }

    // ---------------- Mises à jour ----------------

    private static readonly Color Ok = Color.FromRgb(0x6B, 0xD3, 0x6B), Pending = Color.FromRgb(0xFF, 0xB8, 0x3D), Idle = Color.FromRgb(0x6B, 0x74, 0x84);

    /// <summary>État de Windows Update en une ligne.</summary>
    public static (string Text, Color Color) WindowsStatus(WindowsUpdateStatus? status)
    {
        if (status is null) return ("Vérification…", Idle);
        if (status.Error is not null) return (status.Error, Idle);
        if (status.Count > 0) return ($"{status.Count} mise{(status.Count > 1 ? "s" : "")} à jour disponible{(status.Count > 1 ? "s" : "")}", UpdateBadge.ColorFor(status.Count));
        if (status.RebootRequired) return ("Redémarrage nécessaire", Pending);
        return ("À jour", Ok);
    }

    /// <summary>État d'Armoury Crate en une ligne.</summary>
    public static (string Text, Color Color) AsusStatus(AsusAppInfo? app, bool checkedYet)
    {
        if (!checkedYet) return ("Vérification…", Idle);
        if (app is null) return ("Non installé", Idle);
        return ($"Version {app.Version}", Ok);
    }

    // ---------------- Navigation ----------------

    /// <summary>Rangées de boutons, de haut en bas (un bouton seul, ou ceux d'une rangée de tuiles).</summary>
    private List<List<Button>> NavRows() => Items.Children.OfType<FrameworkElement>()
        .Select(e => e is Button b ? [b] : e is Grid g ? g.Children.OfType<Button>().ToList() : [])
        .Where(r => r.Count > 0).ToList();

    public override bool Handle(NavAction action)
    {
        if (action is not (NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right)) return base.Handle(action);
        if (action is NavAction.Left or NavAction.Right && _brightnessRow?.IsKeyboardFocused == true)
        {
            ChangeBrightness(_brightness + (action == NavAction.Left ? -BrightnessStep : BrightnessStep));
            return true;
        }
        var rows = NavRows();
        var current = Keyboard.FocusedElement as Button;
        var r = rows.FindIndex(row => current is not null && row.Contains(current));
        if (r < 0)
        {
            FocusNav.Focus(rows[0][0]);
            return true;
        }
        double CenterX(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), ItemsHost).X;
        // Depuis un bouton pleine largeur, on arrive sur le premier de la rangée.
        Button? Nearest(List<Button>? row) => rows[r].Count == 1 ? row?[0] : row?.MinBy(b => Math.Abs(CenterX(b) - CenterX(current!)));
        var c = rows[r].IndexOf(current!);
        var target = action switch
        {
            NavAction.Left => rows[r].ElementAtOrDefault(c - 1),
            NavAction.Right => rows[r].ElementAtOrDefault(c + 1),
            NavAction.Up => Nearest(rows.ElementAtOrDefault(r - 1)),
            _ => Nearest(rows.ElementAtOrDefault(r + 1)),
        };
        if (target is not null) FocusNav.Focus(target);
        return true;
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();
}
