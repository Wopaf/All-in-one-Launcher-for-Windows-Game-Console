using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Barre de gauche, à la place de celle de Windows : menu Windows maison en haut, applications ouvertes,
/// puis les applications en arrière-plan en bas.
/// </summary>
public partial class DockBar : UserControl
{
    public event Action? WindowsMenuRequested, BackgroundRequested, HintsChanged, LeaveRequested;
    public event Action<OpenWindow>? AppRequested, MinimizeRequested, CloseRequested;

    private readonly SlideHighlight _highlight;
    /// <summary>Icônes d'applications fermées, le temps de leur disparition : plus sélectionnables, plus comptées.</summary>
    private readonly HashSet<Button> _leaving = [];

    public DockBar()
    {
        InitializeComponent();
        _highlight = new SlideHighlight(Highlight, Host);
        foreach (var b in new[] { MenuButton, BackgroundButton }) Track(b);
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsKeyboardFocusWithin) return;
            _highlight.Hide();
            HideLabel();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Applications lancées sans fenêtre, au dernier relevé.</summary>
    public IReadOnlyList<BackgroundApp> BackgroundApps { get; private set; } = [];

    public string Hints => FocusedWindow is not null
        ? "A:Afficher|Y:Fermer|→:Revenir"
        : "A:Ouvrir|→:Revenir";

    private OpenWindow? FocusedWindow => (Keyboard.FocusedElement as FrameworkElement)?.Tag as OpenWindow;

    /// <summary>
    /// Met à jour la barre. Les icônes gardent leur place (une nouvelle fenêtre s'ajoute à la fin), et rien n'est
    /// reconstruit si les mêmes fenêtres sont ouvertes : la sélection ne bouge pas.
    /// </summary>
    public void SetWindows(List<OpenWindow> windows, List<BackgroundApp> background)
    {
        BackgroundApps = background;

        var buttons = AppButtons();
        var known = buttons.Select(b => ((OpenWindow)b.Tag).Handle).ToList();
        var visible = windows.OrderBy(w => known.IndexOf(w.Handle) is var i and >= 0 ? i : int.MaxValue).ToList();
        if (known.SequenceEqual(visible.Select(w => w.Handle)))
        {
            // Mêmes fenêtres : seul le titre peut avoir changé.
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].Tag = visible[i];
                buttons[i].ToolTip = visible[i].Title;
            }
            if (FocusedWindow is { } current) ShowLabel(Keyboard.FocusedElement as Button, current.Title);
            return;
        }

        // Mise à jour sur place : les icônes restantes ne bougent pas, celles des fenêtres fermées s'en vont en
        // zoom arrière (la place se referme ensuite), les nouvelles arrivent à la fin en zoom avant.
        var handles = windows.Select(w => w.Handle).ToHashSet();
        var index = buttons.FindIndex(b => b.IsKeyboardFocused);
        var gone = buttons.Where(b => !handles.Contains(((OpenWindow)b.Tag).Handle)).ToList();
        foreach (var b in buttons.Except(gone))
        {
            var w = windows.First(x => x.Handle == ((OpenWindow)b.Tag).Handle);
            b.Tag = w;
            b.ToolTip = w.Title;
        }
        foreach (var w in visible.Where(w => !known.Contains(w.Handle)))
        {
            var b = BuildApp(w, null);
            Apps.Children.Add(b);
            Arrive(b);
        }
        var selectedGone = index >= 0 && gone.Contains(buttons[index]);
        foreach (var b in gone) Leave(b, selectedGone && b == buttons[index] ? index : null);
        if (FocusedWindow is { } current2 && !selectedGone) ShowLabel(Keyboard.FocusedElement as Button, current2.Title);
    }

    /// <summary>Applications affichées (sans celles qui sont en train de disparaître).</summary>
    private List<Button> AppButtons() => Apps.Children.OfType<Button>().Where(b => !_leaving.Contains(b)).ToList();

    private const double LeaveMs = 260, CollapseMs = 220;

    /// <summary>Nouvelle application : son icône grandit depuis le centre.</summary>
    private static void Arrive(Button b)
    {
        var scale = new ScaleTransform(0.4, 0.4);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = scale;
        var grow = new DoubleAnimation(0.4, 1, Anim.Ms(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(220)) { EasingFunction = Anim.Out });
    }

    /// <summary>
    /// Application fermée : son icône rétrécit vers son centre en s'effaçant, puis la place se referme en douceur (les
    /// icônes du dessous remontent). Si elle était sélectionnée, la sélection passe ensuite à sa voisine.
    /// </summary>
    /// <param name="selectedIndex">Place de l'icône si elle était sélectionnée.</param>
    private void Leave(Button b, int? selectedIndex)
    {
        _leaving.Add(b);
        b.IsHitTestVisible = false;
        if (selectedIndex is not null)
        {
            _highlight.Hide();
            HideLabel();
        }
        var scale = new ScaleTransform(1, 1);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = scale;
        var shrink = new DoubleAnimation(1, 0.2, Anim.Ms(LeaveMs)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.3 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim.Ms(LeaveMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        // La place se referme pendant la fin du zoom.
        var begin = TimeSpan.FromMilliseconds(LeaveMs * 0.55);
        var close = new DoubleAnimation(b.ActualHeight > 0 ? b.ActualHeight : 52, 0, Anim.Ms(CollapseMs)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        close.Completed += (_, _) =>
        {
            var hadFocus = b.IsKeyboardFocused;
            Apps.Children.Remove(b);
            _leaving.Remove(b);
            if (selectedIndex is not { } index || !hadFocus) return;
            var rest = AppButtons();
            var target = rest.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, rest.Count - 1))) ?? BackgroundButton;
            FocusNav.Focus(target);
        };
        b.BeginAnimation(HeightProperty, close);
        b.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0), Anim.Ms(CollapseMs)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private Button BuildApp(OpenWindow w, Button? existing)
    {
        // Icône déjà chargée : on la réutilise pour éviter un clignotement.
        var image = existing?.Content is Grid { Children: [Image known, ..] } ? known.Source : null;
        var grid = new Grid { Width = 28, Height = 28 };
        var icon = new Image { Width = 26, Height = 26, Source = image ?? AppIcons.Fallback, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        if (image is null && w.IconSource is { } source) LoadIcon(icon, source);

        var b = new Button { Style = (Style)FindResource("DockButton"), Content = grid, Tag = w, ToolTip = w.Title };
        Track(b);
        b.Click += (_, _) => { if (b.Tag is OpenWindow window && !_leaving.Contains(b)) AppRequested?.Invoke(window); };
        return b;
    }

    private static async void LoadIcon(Image image, string source)
    {
        if (await WindowsShell.IconAsync(source) is { } icon) image.Source = icon;
    }

    private void Track(Button b)
    {
        // Le même libellé au survol qu'à la sélection.
        b.MouseEnter += (_, _) => ShowLabel(b, LabelOf(b));
        b.MouseLeave += (_, _) =>
        {
            if (Keyboard.FocusedElement is Button focused && NavButtons().Contains(focused)) ShowLabel(focused, LabelOf(focused));
            else HideLabel();
        };
        b.GotKeyboardFocus += (_, _) =>
        {
            _highlight.MoveTo(b);
            ShowLabel(b, LabelOf(b));
            HintsChanged?.Invoke();
        };
    }

    private static string LabelOf(Button b) => b.Tag is OpenWindow w ? w.Title : b.ToolTip as string ?? "";

    private Button? _labelFor;

    /// <summary>
    /// Infobulle : apparaît en fondu en glissant depuis la barre. D'un bouton à l'autre, elle glisse jusqu'au nouveau
    /// bouton, son texte apparaît en fondu et sa largeur s'adapte en douceur (un simple saut de texte ne se voit pas animé).
    /// </summary>
    private void ShowLabel(Button? b, string text)
    {
        if (b is null) return;
        var changed = LabelText.Text != text;
        LabelText.Text = text;
        LabelText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(Math.Min(LabelText.DesiredSize.Width + Label.Padding.Left + Label.Padding.Right + 6, Label.MaxWidth));
        if (_labelFor == b)
        {
            // Même bouton : le titre de la fenêtre a changé, seule la largeur suit.
            if (changed) Label.BeginAnimation(WidthProperty, new DoubleAnimation(width, Anim.Ms(LabelMoveMs)) { EasingFunction = Anim.Out });
            return;
        }
        var appearing = _labelFor is null;
        _labelFor = b;
        Label.Visibility = Visibility.Visible;
        if (appearing)
        {
            Label.BeginAnimation(WidthProperty, null);
            Label.Width = width;
            LabelMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-12, 0, Anim.Ms(220)) { EasingFunction = Anim.Out });
            Label.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim.Ms(180)) { EasingFunction = Anim.Out });
        }
        else
        {
            Label.BeginAnimation(WidthProperty, new DoubleAnimation(width, Anim.Ms(LabelMoveMs)) { EasingFunction = Anim.Out });
            LabelText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(LabelMoveMs)) { EasingFunction = Anim.Out });
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_labelFor != b || !b.IsVisible) return;
            var y = b.TranslatePoint(new Point(0, (b.ActualHeight - Label.ActualHeight) / 2), Host).Y;
            if (appearing)
            {
                LabelMove.BeginAnimation(TranslateTransform.YProperty, null);
                LabelMove.Y = y;
            }
            else LabelMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Anim.Ms(LabelMoveMs)) { EasingFunction = Anim.Out });
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private const double LabelMoveMs = 260;

    private void HideLabel()
    {
        if (_labelFor is null) return;
        _labelFor = null;
        var fade = new DoubleAnimation(0, Anim.Ms(140)) { EasingFunction = Anim.Out };
        // Réaffichée entre-temps : elle reste visible.
        fade.Completed += (_, _) => { if (_labelFor is null) Label.Visibility = Visibility.Collapsed; };
        Label.BeginAnimation(OpacityProperty, fade);
    }

    private void Menu_Click(object sender, RoutedEventArgs e) => WindowsMenuRequested?.Invoke();
    private void Background_Click(object sender, RoutedEventArgs e) => BackgroundRequested?.Invoke();

    // ---------------- Navigation ----------------

    private List<Button> NavButtons()
    {
        var list = new List<Button> { MenuButton };
        list.AddRange(AppButtons());
        list.Add(BackgroundButton);
        return list;
    }

    /// <summary>Entre dans la barre, sur le bouton du menu Windows.</summary>
    public void Enter() => FocusNav.Focus(MenuButton);

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down:
                // Icône sélectionnée en train de disparaître : la sélection passe à sa voisine juste après.
                if (Keyboard.FocusedElement is Button leaving && _leaving.Contains(leaving)) return true;
                var buttons = NavButtons();
                var i = buttons.IndexOf(Keyboard.FocusedElement as Button ?? MenuButton) + (action == NavAction.Up ? -1 : 1);
                if (i >= 0 && i < buttons.Count) FocusNav.Focus(buttons[i]);
                return true;
            case NavAction.Accept:
                FocusNav.Activate();
                return true;
            case NavAction.Option:
                if (FocusedWindow is { } toMinimize) MinimizeRequested?.Invoke(toMinimize);
                return true;
            case NavAction.Secondary:
                if (FocusedWindow is { } toClose) CloseRequested?.Invoke(toClose);
                return true;
            case NavAction.Back or NavAction.Right:
                LeaveRequested?.Invoke();
                return true;
            case NavAction.Left or NavAction.PrevLetter or NavAction.NextLetter:
                return true;
        }
        return false;
    }
}
