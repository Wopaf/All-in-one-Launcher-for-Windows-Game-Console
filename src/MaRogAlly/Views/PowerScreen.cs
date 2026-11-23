using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MaRogAlly.Views;

/// <summary>
/// Écran plein écran affiché pendant l'arrêt ou le redémarrage de l'appareil, par-dessus tout (jeu compris) :
/// un anneau qui tourne et un petit texte, jusqu'à ce que Windows ferme l'application.
/// </summary>
public sealed class PowerScreen : Window
{
    public PowerScreen(bool restart)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Background = (Brush)FindResource("Bg");
        Foreground = (Brush)FindResource("TextPrimary");
        FontFamily = (FontFamily)FindResource("Body");
        UseLayoutRounding = true;
        Cursor = System.Windows.Input.Cursors.None;

        // Anneau : un quart de cercle à la couleur d'accent qui tourne sur un cercle discret.
        var track = new Ellipse { Width = 64, Height = 64, StrokeThickness = 5, Opacity = 0.18 };
        track.SetResourceReference(Shape.StrokeProperty, "TextPrimary");
        var spin = new RotateTransform();
        var arc = new System.Windows.Shapes.Path
        {
            Width = 64, Height = 64, StrokeThickness = 5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 32,2.5 A 29.5,29.5 0 0 1 61.5,32"),
            RenderTransform = spin, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Accent");
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        var title = new TextBlock
        {
            Text = restart ? "Redémarrage en cours…" : "Arrêt en cours…",
            FontSize = 30, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("Display"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 32, 0, 8),
        };
        var note = new TextBlock
        {
            Text = restart ? "L'appareil redémarre, il revient dans quelques instants." : "L'appareil s'éteint. À bientôt !",
            FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16),
            Children = { new Grid { Children = { track, arc } }, title, note },
        };
        Content = content;

        // Apparition en fondu : l'écran ne surgit pas d'un coup.
        content.Opacity = 0;
        Loaded += (_, _) => content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
    }
}
