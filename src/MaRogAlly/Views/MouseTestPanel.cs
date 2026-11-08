using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaRogAlly.Input;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Essai du mode souris (réglages) : de faux boutons à survoler et cliquer, une zone pour le clic droit, une liste à faire
/// défiler. Pendant l'essai, la manette pilote la souris au lieu de l'application ; B ou Échap le referme.
/// </summary>
public sealed class MouseTestPanel : OverlayBase
{
    private static readonly Color Card = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Hover = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    private readonly TextBlock _status = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };

    public MouseTestPanel()
    {
        _status.Foreground = (Brush)FindResource("TextSecondary");
        _status.Text = "Bouge le curseur sur les boutons, puis clique.";

        var backdrop = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)) };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Tester la souris", Style = (Style)FindResource("H2"), FontSize = 28 });
        content.Children.Add(new TextBlock
        {
            Text = "Stick droit : bouger · RB : clic gauche · LB : clic droit · Stick gauche : défiler · B : quitter",
            FontSize = 14, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 6, 0, 22), TextWrapping = TextWrapping.Wrap,
        });

        // Cibles : elles s'éclairent au survol et comptent les clics gauches.
        var targets = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(-6, 0, -6, 0) };
        for (var i = 1; i <= 6; i++) targets.Children.Add(Target($"Bouton {i}"));
        content.Children.Add(targets);

        var lower = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        lower.ColumnDefinitions.Add(new ColumnDefinition());
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        lower.ColumnDefinitions.Add(new ColumnDefinition());
        lower.Children.Add(RightClickZone());
        var list = ScrollList();
        Grid.SetColumn(list, 2);
        lower.Children.Add(list);
        content.Children.Add(lower);
        content.Children.Add(_status);

        var card = new Border
        {
            Width = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(22), Padding = new Thickness(34, 30, 34, 26), Background = (Brush)FindResource("Bg"),
            Child = content,
        };
        Content = new Grid { Children = { backdrop, card } };
    }

    public override string Hints => "B:Quitter l'essai";

    /// <summary>Rien ne prend le focus : la manette pilote la souris, pas la sélection.</summary>
    protected override void FocusOnOpen() => Window.GetWindow(this)?.Focus();

    public override bool Handle(NavAction action)
    {
        if (action == NavAction.Back) Close();
        return true;
    }

    private Border Target(string label)
    {
        var clicks = 0;
        var text = new TextBlock { Text = label, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var target = new Border
        {
            Height = 76, Margin = new Thickness(6), CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Card), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Child = text,
        };
        target.MouseEnter += (_, _) => target.Background = new SolidColorBrush(Hover);
        target.MouseLeave += (_, _) =>
        {
            target.Background = new SolidColorBrush(Card);
            target.BorderBrush = Brushes.Transparent;
        };
        target.MouseLeftButtonDown += (_, _) => target.BorderBrush = (Brush)FindResource("Accent");
        target.MouseLeftButtonUp += (_, _) =>
        {
            clicks++;
            target.BorderBrush = Brushes.Transparent;
            text.Text = $"{label} · {clicks} clic{(clicks > 1 ? "s" : "")}";
            _status.Text = $"Clic gauche sur {label.ToLowerInvariant()}.";
            Anim.Enter(text, 0, 0, 200);
        };
        return target;
    }

    private Border RightClickZone()
    {
        var text = new TextBlock { Text = "Clic droit ici", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        var zone = new Border
        {
            Height = 170, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Card),
            BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Child = text,
        };
        var count = 0;
        zone.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            count++;
            text.Text = $"Clic droit reçu\n{count} fois";
            _status.Text = "Clic droit.";
            Anim.Enter(text, 0, 0, 200);
        };
        return zone;
    }

    private Border ScrollList()
    {
        var lines = new StackPanel();
        for (var i = 1; i <= 40; i++)
            lines.Children.Add(new TextBlock { Text = $"Ligne {i}", FontSize = 15, Margin = new Thickness(16, 6, 16, 6), Foreground = (Brush)FindResource("TextSecondary") });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = lines };
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0) _status.Text = "Défilement.";
        };
        return new Border { Height = 170, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Card), Child = scroll, ClipToBounds = true };
    }
}
