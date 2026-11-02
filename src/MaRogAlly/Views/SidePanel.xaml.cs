using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Panneau latéral générique (tri, filtres) construit par code.</summary>
public partial class SidePanel : OverlayBase
{
    private string _hints = "A:Valider|B:Fermer";
    private readonly SlideHighlight _highlight;

    public SidePanel(string title, string subtitle = "")
    {
        InitializeComponent();
        _highlight = new SlideHighlight(Highlight, ItemsHost);
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _left;
    protected override FrameworkElement AnimatedPart => Panel;
    protected override double EnterFromX => _left ? -60 : 60;

    /// <summary>Ouvre le panneau contre la barre de gauche ; <paramref name="bottom"/> : posé en bas, à la hauteur de son contenu.</summary>
    public void AnchorLeft(bool bottom)
    {
        _left = true;
        Panel.HorizontalAlignment = HorizontalAlignment.Left;
        if (!bottom) return;
        Panel.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.CornerRadius = new CornerRadius(0, 18, 0, 0);
    }
    protected override FrameworkElement FocusScope => Items;
    public override string Hints => _hints;
    public void SetHints(string hints) => _hints = hints;

    public void Clear()
    {
        _hasGrid = false;
        Items.Children.Clear();
        _highlight.Hide();
    }

    /// <summary>Le cadre de sélection glisse jusqu'à la ligne (comme dans les Réglages et les Plateformes).</summary>
    private void Track(ButtonBase row)
    {
        RowOptions.SetFloating(row, true);
        row.GotKeyboardFocus += (_, _) => _highlight.MoveTo(row);
    }

    public void SetSubtitle(string subtitle)
    {
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AddHeader(string text)
        => Items.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(18, Items.Children.Count == 0 ? 0 : 22, 0, 6),
        });

    /// <summary>Ligne cliquable ; <paramref name="selected"/> affiche une coche d'accent.</summary>
    public Button AddChoice(string label, bool selected, Action onClick, string? detail = null, Color? swatch = null)
    {
        var grid = new Grid();
        if (swatch is { } c)
        {
            // Pastille de couleur devant le libellé (choix de la couleur d'accent).
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 14, 0),
                Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(row);
        }
        else grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (detail is not null)
            right.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("TextMuted"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        if (selected)
            right.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), Foreground = (Brush)FindResource("Accent"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(right);

        var b = new Button { Content = grid, Style = (Style)FindResource("RowButton") };
        Track(b);
        b.Click += (_, _) => onClick();
        Items.Children.Add(b);
        return b;
    }

    /// <summary>Ligne d'application : son icône Windows (chargée après coup), son nom ; ou un glyphe si elle n'a pas d'icône.</summary>
    public Button AddApp(string label, string? iconSource, Action onClick, string? glyph = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        else
        {
            var icon = new Image { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            row.Children.Add(icon);
            if (iconSource is not null) LoadIcon(icon, iconSource);
        }
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });

        var b = new Button { Content = row, Style = (Style)FindResource("RowButton") };
        Track(b);
        b.Click += (_, _) => onClick();
        Items.Children.Add(b);
        return b;
    }

    private bool _hasGrid;

    /// <summary>Grille d'icônes (logo et nom dessous) ; chaque case se sélectionne comme une ligne.</summary>
    public void AddIconGrid(int columns, IEnumerable<(string Logo, string Label, Action OnClick)> items)
    {
        var grid = new UniformGrid { Columns = columns };
        foreach (var (logo, label, onClick) in items)
        {
            var content = new StackPanel { Width = 86 };
            content.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(logo), Fill = Brushes.White, Stretch = Stretch.Uniform, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 0) });
            var b = new Button { Content = content, Style = (Style)FindResource("RowButton"), Padding = new Thickness(0, 10, 0, 10) };
            RowOptions.SetNoBar(b, true);
            Track(b);
            b.Click += (_, _) => onClick();
            grid.Children.Add(b);
        }
        _hasGrid = true;
        Items.Children.Add(grid);
    }

    /// <summary>Avec une grille, le déplacement est explicite : la prédiction de WPF s'y perd entre lignes et cases.</summary>
    public override bool Handle(Input.NavAction action)
    {
        if (!_hasGrid || action is not (Input.NavAction.Up or Input.NavAction.Down or Input.NavAction.Left or Input.NavAction.Right))
            return base.Handle(action);

        var rows = new List<List<ButtonBase>>();
        foreach (var child in Items.Children)
        {
            if (child is ButtonBase row) rows.Add([row]);
            else if (child is UniformGrid g) rows.AddRange(g.Children.OfType<ButtonBase>().Chunk(g.Columns).Select(c => c.ToList()));
        }
        var current = Keyboard.FocusedElement as ButtonBase;
        var r = current is null ? -1 : rows.FindIndex(x => x.Contains(current));
        if (r < 0)
        {
            if (rows.Count > 0) FocusNav.Focus(rows[0][0]);
            return true;
        }
        var c = rows[r].IndexOf(current!);
        double CenterX(ButtonBase b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), Items).X;
        ButtonBase? target = action switch
        {
            Input.NavAction.Left => c > 0 ? rows[r][c - 1] : null,
            Input.NavAction.Right => c < rows[r].Count - 1 ? rows[r][c + 1] : null,
            Input.NavAction.Up => r > 0 ? rows[r - 1].MinBy(b => Math.Abs(CenterX(b) - CenterX(current!))) : null,
            _ => r < rows.Count - 1 ? rows[r + 1].MinBy(b => Math.Abs(CenterX(b) - CenterX(current!))) : null,
        };
        if (target is not null) FocusNav.Focus(target);
        return true;
    }

    private static async void LoadIcon(Image image, string source)
    {
        if (await Services.WindowsShell.IconAsync(source) is { } icon) image.Source = icon;
    }

    /// <summary>Case à cocher (style « CheckRow ») ou interrupteur (style « SwitchRow »).</summary>
    public ToggleButton AddToggle(string label, bool isChecked, Action<bool> onChanged, bool asSwitch = false, string? detail = null)
    {
        object content = label;
        if (detail is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = label });
            sp.Children.Add(new TextBlock { Text = "  " + detail, Foreground = (Brush)FindResource("TextMuted"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var t = new ToggleButton
        {
            Content = content,
            IsChecked = isChecked,
            Style = (Style)FindResource(asSwitch ? "SwitchRow" : "CheckRow"),
        };
        Track(t);
        t.Click += (_, _) => onChanged(t.IsChecked == true);
        Items.Children.Add(t);
        return t;
    }

    /// <summary>Donne le focus à un élément précis à l'ouverture (ex. le tri actuel).</summary>
    public UIElement? InitialFocus { get; set; }

    public override void OnOpened()
    {
        base.OnOpened();
        if (InitialFocus is not null)
            Dispatcher.BeginInvoke(() => UI.FocusNav.Focus(InitialFocus), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();
}
