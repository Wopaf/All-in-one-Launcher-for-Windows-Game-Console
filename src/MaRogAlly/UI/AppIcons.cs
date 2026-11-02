using System.Windows;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>Icône maison pour les applications qui n'en ont pas : l'hexagone du fond de l'application, sur une tuile sombre.</summary>
public static class AppIcons
{
    public static ImageSource Fallback { get; } = Build();

    private static ImageSource Build()
    {
        var tile = new GeometryDrawing(
            new LinearGradientBrush(Color.FromRgb(0x2F, 0x38, 0x48), Color.FromRgb(0x16, 0x1A, 0x23), 90),
            new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1.5),
            new RectangleGeometry(new Rect(1, 1, 62, 62), 14, 14));
        var hexagon = new GeometryDrawing(null,
            new Pen(Brushes.White, 4) { LineJoin = PenLineJoin.Round },
            Geometry.Parse("M32 13 48.5 22.5V41.5L32 51 15.5 41.5V22.5Z"));
        var center = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x7F)), null,
            new EllipseGeometry(new Point(32, 32), 6, 6));
        var image = new DrawingImage(new DrawingGroup { Children = { tile, hexagon, center } });
        image.Freeze();
        return image;
    }
}
