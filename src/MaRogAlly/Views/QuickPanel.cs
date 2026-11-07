using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Windows.Devices.Radios;

namespace MaRogAlly.Views;

/// <summary>
/// Centre de contrôle (bouton M1), ouvert par-dessus n'importe quelle application : accueil, performance, audio, affichage, galerie.
/// Les gâchettes changent d'onglet, la croix navigue, gauche et droite règlent les curseurs.
/// </summary>
public sealed class QuickPanel : UserControl
{
    private const int QuickColumns = 4, GalleryLimit = 60, KeepModeSeconds = 15, BrightnessMin = 10, Step = 10;
    private static readonly string[] Names = ["Accueil", "Performance", "Audio", "Affichage", "Galerie"];
    private static readonly string[] Glyphs = ["", "", "", "", ""];
    private static HashSet<RadioKind>? _beforeAirplane;

    private readonly OpenWindow? _target;
    private readonly Func<ControlMode?> _readMode;
    private readonly Action<ControlMode> _writeMode;
    private readonly bool _hasController, _interactive;

    private readonly Grid _root = new();
    private const double TabWidth = 56, TabGap = 3;
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _tabPill = new() { Width = TabWidth, Height = 40, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly TranslateTransform _tabPillMove = new();
    private bool _tabPillPlaced;
    private readonly TextBlock _title = new() { FontSize = 26 };
    private readonly StackPanel _items = new();
    private readonly Border _box = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) };
    private readonly Grid _itemsHost = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _status = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _hints = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly SlideHighlight _highlight;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<Button, Action<int>> _adjust = [];

    private int _tab, _ticks, _screen;
    private Action? _onTick, _back;
    private Func<NavAction, bool>? _screenKeys;
    private DisplayMode? _tryingMode;
    private SystemStats? _stats;
    private readonly object _statsLock = new();
    private bool _sampling, _closed;

    public event Action? CloseRequested, HomeRequested, DesktopRequested, ScreenshotRequested, WifiRequested, BluetoothRequested;
    /// <summary>Une image de la galerie est choisie : liste des images, et position de celle-ci.</summary>
    public event Action<List<string>, int>? ImageRequested;

    /// <param name="interactive">Faux pour une capture de vérification : aucun élément ne prend le focus, qui resterait sinon à l'utilisateur.</param>
    public QuickPanel(OpenWindow? target, Func<ControlMode?> readMode, Action<ControlMode> writeMode, bool hasController, int tab, bool interactive = true)
    {
        _interactive = interactive;
        _target = target;
        _readMode = readMode;
        _writeMode = writeMode;
        _hasController = hasController;
        _tab = tab;
        _highlight = new SlideHighlight(_box, _itemsHost);
        _ticker.Tick += (_, _) => { _ticks++; _onTick?.Invoke(); };
        BuildLayout();
        Loaded += (_, _) => ShowTab(_tab);
    }

    /// <summary>Le contenu arrive un peu après la fenêtre qui glisse, et se pose en dernier.</summary>
    public void PlayEntrance() => Anim.Enter(_root, dx: -48, dy: 0, ms: 360);

    /// <summary>Le contenu s'efface pendant que la fenêtre repart.</summary>
    public void PlayExit() => Anim.Exit(_root, dx: -32, ms: 170);

    // ================= Structure =================

    private void BuildLayout()
    {
        _title.FontFamily = (FontFamily)FindResource("Display");
        _title.FontWeight = FontWeights.Bold;
        _status.Foreground = (Brush)FindResource("Gold");

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Margin = new Thickness(24, 22, 24, 18);

        _root.Children.Add(BuildClock());

        var bar = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = PadGlyph("LT");
        previous.Margin = new Thickness(0);
        previous.VerticalAlignment = VerticalAlignment.Center;
        previous.MouseLeftButtonUp += (_, _) => ShowTab(_tab - 1);
        bar.Children.Add(previous);
        // La pastille de l'onglet choisi glisse derrière les icônes, en blanc discret : la couleur d'accent reste aux réglages activés.
        _tabPill.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        _tabPill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        _tabPill.BorderThickness = new Thickness(1);
        _tabPill.RenderTransform = _tabPillMove;
        var tabs = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { _tabPill, _tabBar } };
        Grid.SetColumn(tabs, 1);
        bar.Children.Add(tabs);
        var next = PadGlyph("RT");
        next.Margin = new Thickness(0);
        next.VerticalAlignment = VerticalAlignment.Center;
        next.MouseLeftButtonUp += (_, _) => ShowTab(_tab + 1);
        Grid.SetColumn(next, 2);
        bar.Children.Add(next);
        Grid.SetRow(bar, 1);
        _root.Children.Add(bar);

        for (var i = 0; i < Names.Length; i++)
        {
            var index = i;
            var tab = new Border
            {
                Width = TabWidth,
                Height = 40,
                Margin = new Thickness(TabGap, 0, TabGap, 0),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = Glyphs[i], FontFamily = (FontFamily)FindResource("Icons"), FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            tab.MouseLeftButtonUp += (_, _) => ShowTab(index);
            _tabBar.Children.Add(tab);
        }

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _title.Margin = new Thickness(4, 0, 0, 12);
        body.Children.Add(_title);
        // Le cadre de sélection passe devant : derrière une vignette de la galerie, il serait caché par l'image.
        Panel.SetZIndex(_box, 1);
        _itemsHost.Children.Add(_box);
        _itemsHost.Children.Add(_items);
        var scroll = new ScrollViewer { Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, PanningMode = PanningMode.VerticalOnly, Content = _itemsHost };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        Grid.SetRow(_status, 2);
        body.Children.Add(_status);
        Grid.SetRow(body, 2);
        _root.Children.Add(body);

        Grid.SetRow(_hints, 3);
        _root.Children.Add(_hints);

        // Le fond du volet est légèrement translucide : le fond opaque dessous empêche le jeu de transparaître.
        var round = new CornerRadius(22);
        // L'ombre est portée par ce fond immobile : elle n'est pas recalculée quand le contenu s'anime.
        var shaded = new Border { CornerRadius = round, Background = (Brush)FindResource("Bg"), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 36, ShadowDepth = 0, Opacity = 0.55 } };
        Content = new Grid { Children = { shaded, new Border { CornerRadius = round, Background = (Brush)FindResource("PanelBg") }, _root } };
    }

    private FrameworkElement BuildClock()
    {
        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontFamily = (FontFamily)FindResource("Display"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 18,
        });
        var power = Power.Read();
        if (power.HasBattery)
        {
            var battery = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            battery.Children.Add(new TextBlock { Text = Power.Glyph(power), FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            battery.Children.Add(new TextBlock { Text = $"{power.Percent} %", FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(battery);
        }
        return header;
    }

    private Border PadGlyph(string text) => new()
    {
        Style = (Style)FindResource("PadGlyph"),
        Cursor = Cursors.Hand,
        Child = new TextBlock { Text = text, Style = (Style)FindResource("PadGlyphText") },
    };

    private void SetHints(params (string Button, string Label)[] hints)
    {
        _hints.Children.Clear();
        foreach (var (button, label) in hints)
        {
            _hints.Children.Add(PadGlyph(button));
            _hints.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) });
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTab(int index)
    {
        // Un essai d'affichage non confirmé ne survit pas à un changement d'onglet.
        CancelModeTrial();
        _tab = (index + Names.Length) % Names.Length;
        var x = TabGap + _tab * (TabWidth + 2 * TabGap);
        if (_tabPillPlaced) _tabPillMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Anim.Ms(260)) { EasingFunction = Anim.Spring });
        else _tabPillMove.X = x;
        _tabPillPlaced = true;
        BuildTab();
    }

    private void BuildTab()
    {
        BeginScreen(Names[_tab]);
        SetHints(("A", "Valider"), ("B", "Fermer"));
        switch (_tab)
        {
            case 0: BuildHome(); break;
            case 1: BuildPerformance(); break;
            case 2: BuildAudio(); break;
            case 3: BuildDisplay(); break;
            default: BuildGallery(); break;
        }
        FocusFirst();
    }

    /// <summary>Vide le contenu. Numéro d'écran : un résultat asynchrone arrivé après un changement d'onglet est ignoré.</summary>
    private int BeginScreen(string title)
    {
        _title.Text = title;
        _items.Children.Clear();
        _adjust.Clear();
        _onTick = null;
        _back = null;
        _screenKeys = null;
        _ticker.Stop();
        SetStatus("");
        return ++_screen;
    }

    private void FocusFirst()
    {
        var first = NavRows().FirstOrDefault()?.FirstOrDefault();
        _box.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
        if (!_interactive)
        {
            // Capture de vérification : le cadre de sélection s'affiche, sans prendre le focus.
            if (first is not null) _highlight.MoveTo(first);
            return;
        }
        if (first is not null) Dispatcher.BeginInvoke(() => FocusNav.Focus(first), DispatcherPriority.Loaded);
        else Window.GetWindow(this)?.Focus();
    }

    // ================= Navigation =================

    private List<List<Button>> NavRows() => _items.Children.OfType<FrameworkElement>()
        .Select(e => e is Button b ? [b] : e is Grid g ? g.Children.OfType<Button>().ToList() : new List<Button>())
        .Where(r => r.Count > 0).ToList();

    public void Handle(NavAction action)
    {
        if (_screenKeys?.Invoke(action) == true) return;
        switch (action)
        {
            case NavAction.PrevLetter or NavAction.PrevTab:
                ShowTab(_tab - 1);
                break;
            case NavAction.NextLetter or NavAction.NextTab:
                ShowTab(_tab + 1);
                break;
            case NavAction.Back:
                if (_back is { } back) back();
                else CloseRequested?.Invoke();
                break;
            case NavAction.Accept:
                if (!FocusNav.Activate()) FocusFirst();
                break;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                Move(action);
                break;
        }
    }

    private void Move(NavAction action)
    {
        if (action is NavAction.Left or NavAction.Right && Keyboard.FocusedElement is Button slider && _adjust.TryGetValue(slider, out var adjust))
        {
            adjust(action == NavAction.Left ? -1 : 1);
            return;
        }
        var rows = NavRows();
        if (rows.Count == 0) return;
        var current = Keyboard.FocusedElement as Button;
        var r = rows.FindIndex(row => current is not null && row.Contains(current));
        if (r < 0)
        {
            FocusNav.Focus(rows[0][0]);
            return;
        }
        double CenterX(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), _itemsHost).X;
        Button? Nearest(List<Button>? row) => row?.MinBy(b => Math.Abs(CenterX(b) - CenterX(current!)));
        var c = rows[r].IndexOf(current!);
        var target = action switch
        {
            NavAction.Left => rows[r].ElementAtOrDefault(c - 1),
            NavAction.Right => rows[r].ElementAtOrDefault(c + 1),
            NavAction.Up => Nearest(rows.ElementAtOrDefault(r - 1)),
            _ => Nearest(rows.ElementAtOrDefault(r + 1)),
        };
        if (target is not null) FocusNav.Focus(target);
    }

    // ================= Éléments =================

    private Button Track(Button b, Action onClick)
    {
        RowOptions.SetFloating(b, true);
        b.GotKeyboardFocus += (_, _) => _highlight.MoveTo(b);
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button RowButton(FrameworkElement content, double height, Action onClick)
    {
        // Le modèle de ligne centre son contenu : le fond prend toute la hauteur du bouton.
        content.Height = height;
        return Track(new Button
        {
            Style = (Style)FindResource("RowButton"),
            Padding = new Thickness(0),
            Height = height,
            Margin = new Thickness(0),
            Content = content,
        }, onClick);
    }

    private static Border Card(UIElement child, Thickness padding) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
        CornerRadius = new CornerRadius(14),
        Padding = padding,
        Child = child,
    };

    private void AddSection(string title) => _items.Children.Add(new TextBlock
    {
        Text = title.ToUpperInvariant(),
        Style = (Style)FindResource("Label"),
        Margin = new Thickness(4, 12, 0, 8),
    });

    private void AddNote(string text) => _items.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("TextMuted"),
        Margin = new Thickness(4, 10, 4, 0),
    });

    private sealed record Slider(Button Row, Action<bool> SetMuted, TextBlock Icon);

    private Slider AddSlider(string glyph, string? name, int percent, int min, Action<int> apply, Action? onClick = null, int? insertAt = null)
    {
        var value = percent;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);

        var bar = new Grid { Background = Brushes.Transparent, Height = 24 };
        bar.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        var fill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Accent") };
        bar.Children.Add(fill);
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (name is not null) middle.Children.Add(new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        middle.Children.Add(bar);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var text = new TextBlock { FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var muted = false;
        void Show()
        {
            text.Text = muted ? "Muet" : $"{value} %";
            fill.Width = muted ? 0 : bar.ActualWidth * value / 100;
        }
        void Change(int next)
        {
            next = Math.Clamp(next, min, 100);
            if (next == value) return;
            value = next;
            Show();
            apply(value);
        }
        bar.SizeChanged += (_, _) => Show();

        var row = RowButton(Card(grid, new Thickness(20, 0, 20, 0)), name is null ? 56 : 66, onClick ?? (() => { }));
        row.Margin = new Thickness(0, 0, 0, 8);
        _adjust[row] = direction => Change((value / Step + direction) * Step);
        void Drag(double x) { if (bar.ActualWidth > 0) Change((int)Math.Round(x / bar.ActualWidth * 100 / Step) * Step); }
        bar.MouseLeftButtonDown += (_, e) =>
        {
            FocusNav.Focus(row);
            bar.CaptureMouse();
            Drag(e.GetPosition(bar).X);
            e.Handled = true;
        };
        bar.MouseMove += (_, e) => { if (bar.IsMouseCaptured) Drag(e.GetPosition(bar).X); };
        bar.MouseLeftButtonUp += (_, e) =>
        {
            bar.ReleaseMouseCapture();
            e.Handled = true;
        };
        if (insertAt is { } at) _items.Children.Insert(at, row);
        else _items.Children.Add(row);
        Show();
        return new Slider(row, m => { muted = m; Show(); }, icon);
    }

    /// <summary>Réglage activé : dégradé à la couleur d'accent.</summary>
    private void Paint(Border card, bool active)
    {
        var accent = (Color)FindResource("AccentColor");
        Color Tint(byte alpha) => Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        card.BorderThickness = new Thickness(1);
        card.Background = active
            ? new LinearGradientBrush(new GradientStopCollection { new(Tint(0x4D), 0), new(Tint(0x14), 1) }, new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
        card.BorderBrush = active ? new LinearGradientBrush(Tint(0x59), Tint(0x14), new Point(0, 0), new Point(1, 1)) : Brushes.Transparent;
    }

    private List<Border> AddTiles(double height, params (string? Glyph, string Label, Action OnClick)[] tiles) => AddTileRow(height, tiles.Length, tiles);

    /// <summary>Rangée de tuiles de même taille : icône (facultative) au-dessus du libellé. Une rangée incomplète garde des colonnes vides.</summary>
    private List<Border> AddTileRow(double height, int columns, (string? Glyph, string Label, Action OnClick)[] tiles)
    {
        // Même marge de chaque côté de toutes les tuiles, que la grille rattrape sur ses bords : aucune n'est plus large qu'une autre.
        var grid = new Grid { Margin = new Thickness(-4, 0, -4, 8) };
        var cards = new List<Border>();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < tiles.Length; i++)
        {
            var (glyph, label, onClick) = tiles[i];
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (glyph is not null) content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(new TextBlock { Text = label, FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            var card = Card(content, new Thickness(6, 0, 6, 0));
            card.Height = height;
            cards.Add(card);
            var button = RowButton(card, height, onClick);
            button.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }
        _items.Children.Add(grid);
        return cards;
    }

    // ================= Accueil =================

    private void BuildHome()
    {
        var screen = _screen;
        AddSection("Mode");
        var modes = new[] { (ControlMode.Auto, "Auto"), (ControlMode.Gamepad, "Manette"), (ControlMode.Desktop, "Bureau") };
        List<Border> modeCards = [];
        void PaintModes()
        {
            for (var i = 0; i < modes.Length; i++) Paint(modeCards[i], _readMode() == modes[i].Item1);
        }
        modeCards = AddTiles(48, modes.Select(m => ((string?)null, m.Item2, (Action)(() =>
        {
            if (!_hasController)
            {
                SetStatus("Le mode de la manette n'existe que sur la ROG Ally.");
                return;
            }
            _writeMode(m.Item1);
            PaintModes();
        }))).ToArray());
        PaintModes();

        AddSection("Accès rapides");
        AddTileRow(84, QuickColumns,
        [
            ("", "Fin de tâche", EndTask),
            ("", "Afficher le bureau", () => DesktopRequested?.Invoke()),
            ("", "Accueil", () => HomeRequested?.Invoke()),
            ("", "Capture d'écran", () => ScreenshotRequested?.Invoke()),
        ]);
        var radios = AddTileRow(84, QuickColumns,
        [
            ("", "Wi-Fi", () => WifiRequested?.Invoke()),
            ("", "Bluetooth", () => BluetoothRequested?.Invoke()),
            ("", "Mode avion", () => _ = ToggleAirplaneAsync()),
            ("\uE706", "Luminosité auto", () => _ = ToggleAutoBrightnessAsync()),
        ]);
        _radioCards = radios;
        _autoBrightnessCard = radios[3];
        _ = PaintRadiosAsync(screen);
        _ = PaintAutoBrightnessAsync(screen);

        _items.Children.Add(new Border { Height = 12 });
        Button? volumeRow = null;
        if (AudioMixer.ReadMaster() is { } master)
        {
            var muted = master.Muted;
            Slider? volume = null;
            volume = AddSlider(muted ? "" : "", null, master.Percent, 0, AudioMixer.SetMaster, () =>
            {
                muted = !muted;
                AudioMixer.SetMasterMuted(muted);
                volume!.SetMuted(muted);
                volume.Icon.Text = muted ? "" : "";
            });
            volume.SetMuted(muted);
            volumeRow = volume.Row;
        }
        _ = AddBrightnessAsync(screen, volumeRow);
    }

    /// <summary>Remet à jour Wi-Fi, Bluetooth et mode avion : un interrupteur a pu changer depuis leur fenêtre.</summary>
    public void RefreshRadios() => _ = PaintRadiosAsync(_screen);

    private async Task AddBrightnessAsync(int screen, Button? volumeRow)
    {
        if (await ScreenBrightness.ReadAsync() is not int level || screen != _screen) return;
        // Au-dessus du volume s'il existe, en respectant l'arrondi du curseur.
        var rounded = Math.Clamp((int)Math.Round(level / (double)Step, MidpointRounding.AwayFromZero) * Step, BrightnessMin, 100);
        AddSlider("", null, rounded, BrightnessMin, ScreenBrightness.Set, insertAt: volumeRow is null ? null : _items.Children.IndexOf(volumeRow));
        if (Keyboard.FocusedElement is Button focused) _highlight.MoveTo(focused);
    }

    private List<Border>? _radioCards;

    private async Task PaintRadiosAsync(int screen)
    {
        var all = await Radios.AllAsync();
        if (screen != _screen || _radioCards is not [var wifi, var bluetooth, var airplane, ..]) return;
        Paint(wifi, all.Any(r => r.Kind == RadioKind.WiFi && r.State == RadioState.On));
        Paint(bluetooth, all.Any(r => r.Kind == RadioKind.Bluetooth && r.State == RadioState.On));
        Paint(airplane, all.Count > 0 && all.All(r => r.State != RadioState.On));
    }

    private Border? _autoBrightnessCard;

    private async Task PaintAutoBrightnessAsync(int screen)
    {
        var on = await Task.Run(() => AutoBrightness.Read());
        if (screen == _screen && _autoBrightnessCard is { } card) Paint(card, on == true);
    }

    private async Task ToggleAutoBrightnessAsync()
    {
        var screen = _screen;
        if (!await Task.Run(() => AutoBrightness.HasSensor))
        {
            SetStatus("Pas de capteur de lumière sur cette machine : Windows ne peut pas régler la luminosité tout seul.");
            return;
        }
        var on = await Task.Run(() => AutoBrightness.Read());
        if (on is not { } current || !await Task.Run(() => AutoBrightness.Set(!current))) SetStatus("Windows a refusé de changer la luminosité automatique.");
        await PaintAutoBrightnessAsync(screen);
    }

    /// <summary>Coupe toutes les radios et retient celles qui étaient allumées, pour les rallumer ensuite.</summary>
    private async Task ToggleAirplaneAsync()
    {
        var screen = _screen;
        var all = await Radios.AllAsync();
        if (all.Count == 0)
        {
            SetStatus("Aucune connexion sans fil sur cette machine.");
            return;
        }
        var on = all.Where(r => r.State == RadioState.On).ToList();
        var refused = false;
        if (on.Count > 0)
        {
            _beforeAirplane = on.Select(r => r.Kind).ToHashSet();
            foreach (var radio in on) refused |= !await Radios.SetAsync(radio, false);
        }
        else
        {
            foreach (var radio in all.Where(r => _beforeAirplane?.Contains(r.Kind) != false)) refused |= !await Radios.SetAsync(radio, true);
            _beforeAirplane = null;
        }
        if (refused) SetStatus("Windows a refusé de changer certaines connexions.");
        await PaintRadiosAsync(screen);
    }

    private void EndTask()
    {
        if (_target is not { } target)
        {
            SetStatus("Aucune application à fermer.");
            return;
        }
        var screen = BeginScreen("Fin de tâche");
        _back = BuildTab;
        SetHints(("A", "Valider"), ("B", "Annuler"));
        _items.Children.Add(new TextBlock { Text = $"Forcer la fermeture de « {target.Title} » ?", FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) });
        AddNote("Ce qui n'a pas été enregistré sera perdu.");
        _items.Children.Add(new Border { Height = 16 });
        AddTiles(56,
            (null, "Annuler", () => BuildTab()),
            (null, "Forcer la fermeture", () =>
            {
                if (WindowsShell.EndProcess(target.Handle)) CloseRequested?.Invoke();
                else SetStatus("Windows a refusé de fermer cette application.");
            }));
        FocusFirst();
        _ = screen;
    }

    // ================= Performance =================

    private void BuildPerformance()
    {
        var screen = _screen;
        SetHints(("B", "Fermer"));
        var cpu = AddStat("Processeur (CPU)");
        var gpu = AddStat("Carte graphique (GPU)");
        var vram = AddStat("Mémoire graphique (VRAM)");
        var ram = AddStat("Mémoire vive (RAM)");
        var fps = AddStat("Images par seconde (FPS)");
        fps("—", null);
        AddNote("Les températures ne sont lues que sur les machines ASUS. Windows n'expose pas celles de la RAM et de la VRAM, et la mesure des FPS demande les droits d'administrateur.");

        static string Gb(double bytes) => $"{bytes / (1024 * 1024 * 1024):0.0}";
        static string Usage(double? percent, int? temperature)
            => (percent is { } p ? $"{p:0} %" : "—") + (temperature is { } t ? $"  ·  {t} °C" : "");

        _onTick = () =>
        {
            if (_sampling) return;
            _sampling = true;
            _ = Task.Run(() =>
            {
                lock (_statsLock)
                {
                    if (_closed) return null;
                    _stats ??= new SystemStats();
                    return _stats.Read();
                }
            }).ContinueWith(t =>
            {
                _sampling = false;
                if (screen != _screen || t.Status != TaskStatus.RanToCompletion || t.Result is not { } s) return;
                cpu(Usage(s.Cpu, s.CpuTemp), s.Cpu / 100);
                gpu(Usage(s.Gpu, s.GpuTemp), s.Gpu / 100);
                vram(s.VramUsed is { } used
                    ? s.VramTotal is { } total ? $"{Gb(used)} / {Gb(total)} Go" : $"{Gb(used)} Go"
                    : "—", s.VramUsed is { } u && s.VramTotal is > 0 ? (double)u / s.VramTotal : null);
                ram($"{Gb(s.RamUsed)} / {Gb(s.RamTotal)} Go", s.RamUsed / s.RamTotal);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        };
        _onTick();
        _ticker.Start();
    }

    private Action<string, double?> AddStat(string name)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBlock { Text = "…", FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right };
        grid.Children.Add(value);
        var bar = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        var fill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Accent") };
        bar.Children.Add(fill);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        var card = Card(grid, new Thickness(18, 12, 18, 14));
        card.Margin = new Thickness(0, 0, 0, 8);
        _items.Children.Add(card);
        return (text, fraction) =>
        {
            value.Text = text;
            bar.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;
            // Transition lente : la barre suit la mesure sans à-coups. Une valeur invalide ne doit pas casser l'animation.
            if (fraction is { } f && double.IsFinite(f))
                fill.BeginAnimation(WidthProperty, new DoubleAnimation(bar.ActualWidth * Math.Clamp(f, 0, 1), Anim.Ms(900)) { EasingFunction = Anim.InOut });
        };
    }

    // ================= Audio =================

    private void BuildAudio()
    {
        var apps = AudioMixer.ReadApps();
        if (apps.Count == 0) AddNote("Aucune application n'utilise le son pour le moment.");
        SetHints(("←→", "Volume"), ("A", "Couper"), ("B", "Fermer"));
        foreach (var app in apps)
        {
            var muted = app.Muted;
            Slider? slider = null;
            slider = AddSlider("", app.Name, app.Percent, 0, percent => AudioMixer.SetApp(app.ProcessId, percent), () =>
            {
                muted = !muted;
                AudioMixer.SetAppMuted(app.ProcessId, muted);
                slider!.SetMuted(muted);
            });
            slider.SetMuted(muted);
        }

        // Une application qui ouvre ou ferme le son : la liste se met à jour, la sélection reste à sa place.
        var known = apps.Select(a => a.ProcessId).ToHashSet();
        _onTick = () =>
        {
            if (_ticks % 3 != 0 || AudioMixer.ReadApps().Select(a => a.ProcessId).ToHashSet().SetEquals(known)) return;
            BuildTab();
        };
        _ticker.Start();
    }

    // ================= Affichage =================

    private void BuildDisplay()
    {
        var modes = DisplayModes.All();
        var current = DisplayModes.Current();
        if (modes.Count == 0 || current is null)
        {
            AddNote("Windows ne donne pas les modes de cet écran.");
            return;
        }
        SetHints(("←→", "Changer"), ("A", "Valider"), ("B", "Fermer"));

        var sizes = modes.Select(m => (m.Width, m.Height)).Distinct().ToList();
        var size = Math.Max(0, sizes.IndexOf((current.Width, current.Height)));
        List<int> Rates() => modes.Where(m => (m.Width, m.Height) == sizes[size]).Select(m => m.Hertz).ToList();
        var rate = current.Hertz;

        // Gauche : plus petit, droite : plus grand (les modes sont rangés du plus grand au plus petit).
        Action<string, bool, bool>? showSize = null, showRate = null;
        void Show()
        {
            var rates = Rates();
            var r = rates.IndexOf(rate);
            showSize!($"{sizes[size].Width} × {sizes[size].Height}", size < sizes.Count - 1, size > 0);
            showRate!($"{rate} Hz", r < rates.Count - 1, r > 0);
        }
        showSize = AddChoice("Résolution", direction =>
        {
            size = Math.Clamp(size - direction, 0, sizes.Count - 1);
            var rates = Rates();
            if (!rates.Contains(rate)) rate = rates[0];
            Show();
        });
        showRate = AddChoice("Fréquence de rafraîchissement", direction =>
        {
            var rates = Rates();
            rate = rates[Math.Clamp(rates.IndexOf(rate) - direction, 0, rates.Count - 1)];
            Show();
        });
        Show();
        AddTiles(56, (null, "Appliquer", () =>
        {
            var target = new DisplayMode(sizes[size].Width, sizes[size].Height, rate);
            if (target == DisplayModes.Current()) SetStatus("C'est déjà le réglage de l'écran.");
            else if (DisplayModes.Try(target)) ConfirmMode(target);
            else SetStatus("Cet écran refuse ce réglage.");
        }));
        AddNote($"Un changement revient tout seul à l'ancien réglage après {KeepModeSeconds} secondes s'il n'est pas confirmé.");
    }

    /// <summary>Choix réglé avec gauche et droite : des flèches de part et d'autre de la valeur, atténuées en bout de liste.</summary>
    private Action<string, bool, bool> AddChoice(string name, Action<int> change)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Button? row = null;
        TextBlock Arrow(string glyph, int direction, int column)
        {
            var arrow = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 8, 8, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            arrow.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (row is not null) FocusNav.Focus(row);
                change(direction);
            };
            Grid.SetColumn(arrow, column);
            grid.Children.Add(arrow);
            return arrow;
        }
        var left = Arrow("\uE76B", -1, 1);
        left.Margin = new Thickness(8, 0, 0, 0);
        var value = new TextBlock { FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, MinWidth = 96 };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        var right = Arrow("\uE76C", 1, 3);
        row = RowButton(Card(grid, new Thickness(18, 0, 10, 0)), 60, () => { });
        row.Margin = new Thickness(0, 0, 0, 8);
        _adjust[row] = change;
        _items.Children.Add(row);
        return (text, canLeft, canRight) =>
        {
            value.Text = text;
            left.Opacity = canLeft ? 1 : 0.25;
            right.Opacity = canRight ? 1 : 0.25;
        };
    }

    private void ConfirmMode(DisplayMode target)
    {
        _tryingMode = target;
        BeginScreen("Affichage");
        SetHints(("A", "Valider"), ("B", "Annuler"));
        _items.Children.Add(new TextBlock { Text = "Conserver cet affichage ?", FontSize = 18, Margin = new Thickness(4, 0, 4, 8) });
        var left = KeepModeSeconds;
        var countdown = new TextBlock { FontSize = 14, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(4, 0, 4, 16) };
        void ShowLeft() => countdown.Text = $"Retour à l'ancien réglage dans {left} s";
        ShowLeft();
        _items.Children.Add(countdown);
        void Cancel()
        {
            CancelModeTrial();
            BuildTab();
        }
        _back = Cancel;
        AddTiles(56,
            (null, "Annuler", Cancel),
            (null, "Conserver", () =>
            {
                _tryingMode = null;
                var kept = DisplayModes.Keep(target);
                BuildTab();
                if (!kept) SetStatus("Windows n'a pas pu enregistrer ce réglage.");
            }));
        _onTick = () =>
        {
            left--;
            ShowLeft();
            if (left <= 0) Cancel();
        };
        _ticker.Start();
        FocusFirst();
    }

    private void CancelModeTrial()
    {
        if (_tryingMode is null) return;
        _tryingMode = null;
        DisplayModes.Restore();
    }

    // ================= Galerie =================

    private void BuildGallery()
    {
        var screen = _screen;
        var files = ScreenCapture.List(GalleryLimit);
        if (files.Count == 0)
        {
            AddNote("Aucune capture pour le moment. Elles apparaîtront ici après un appui sur « Capture d'écran » dans l'accueil.");
            return;
        }
        SetHints(("A", "Agrandir"), ("B", "Fermer"));
        for (var i = 0; i < files.Count; i += 2)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            for (var j = i; j < Math.Min(i + 2, files.Count); j++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var index = j;
                var image = new Image { Stretch = Stretch.UniformToFill };
                var frame = new Border { Height = 112, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), Child = image };
                Rounded.SetRadius(frame, 12);
                var button = RowButton(frame, 112, () => ImageRequested?.Invoke(files, index));
                button.Margin = new Thickness(j == i ? 0 : 4, 0, j == i ? 4 : 0, 0);
                Grid.SetColumn(button, j - i);
                grid.Children.Add(button);
                _ = LoadThumbnailAsync(image, files[j], screen);
            }
            // Dernière rangée impaire : la vignette garde sa largeur.
            if (grid.ColumnDefinitions.Count == 1) grid.ColumnDefinitions.Add(new ColumnDefinition());
            _items.Children.Add(grid);
        }
    }

    private async Task LoadThumbnailAsync(Image image, string path, int screen)
    {
        var thumbnail = await Task.Run(() => ScreenCapture.Load(path, 400));
        if (screen == _screen) image.Source = thumbnail;
    }

    /// <summary>Volet fermé : plus de mesures, et un essai d'affichage non confirmé est annulé.</summary>
    public void Shutdown()
    {
        _ticker.Stop();
        CancelModeTrial();
        _screen++;
        Task.Run(() =>
        {
            lock (_statsLock)
            {
                _closed = true;
                _stats?.Dispose();
                _stats = null;
            }
        });
    }
}
