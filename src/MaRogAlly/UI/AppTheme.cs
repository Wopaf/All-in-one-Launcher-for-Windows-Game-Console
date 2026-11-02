using System.Windows;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>
/// Thème de l'application : couleur du fond, des panneaux et lueur d'ambiance. Les ressources
/// « Bg », « PanelBg » et « Ambient » sont utilisées en DynamicResource : les remplacer repeint tout.
/// </summary>
public static class AppTheme
{
    public sealed record Theme(string Id, string Name, Color Bg, Color Panel, Color? Glow, Color? Glow2 = null);

    public const string Default = "nuit";

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly IReadOnlyList<Theme> Choices =
    [
        new("nuit", "Nuit ROG", C("#0A0C11"), C("#F2121620"), null),
        new("carbone", "Carbone", C("#050505"), C("#F5101010"), null),
        new("minuit", "Minuit", C("#060A17"), C("#F20C1326"), C("#2F6BFF")),
        new("aurore", "Aurore boréale", C("#070B12"), C("#F20D1520"), C("#19C3A6"), C("#7A4DFF")),
        new("foret", "Forêt", C("#060E0A"), C("#F20B1712"), C("#1F9D5A")),
        new("braise", "Braise", C("#100806"), C("#F21B0F0B"), C("#FF5A1F"), C("#B3122E")),
        new("crepuscule", "Crépuscule", C("#0D0714"), C("#F2170E22"), C("#C2338C"), C("#5B2EFF")),
        new("ardoise", "Ardoise", C("#161B23"), C("#F2202733"), C("#6B85A8")),
    ];

    public static Theme Find(string? id) => Choices.FirstOrDefault(t => t.Id == id) ?? Choices[0];

    public static void Apply(string? id)
    {
        var t = Find(id);
        var res = Application.Current.Resources;
        res["BgColor"] = t.Bg;
        res["Bg"] = Frozen(new SolidColorBrush(t.Bg));
        res["PanelBg"] = Frozen(new SolidColorBrush(t.Panel));
        res["Ambient"] = Frozen(Ambient(t));
    }

    /// <summary>Lueurs colorées du fond (en haut à droite, et en bas à gauche pour les thèmes à deux couleurs).</summary>
    private static Brush Ambient(Theme t)
    {
        if (t.Glow is not { } g) return Brushes.Transparent;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        group.Children.Add(Spot(g, new Point(0.85, 0.05), 0.75, 0x40));
        group.Children.Add(Spot(t.Glow2 ?? g, new Point(0.1, 1.0), 0.7, (byte)(t.Glow2 is null ? 0x22 : 0x38)));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    private static GeometryDrawing Spot(Color c, Point center, double radius, byte alpha)
    {
        var brush = new RadialGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
        {
            Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius * 1.4,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        return new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1, 1)));
    }

    /// <summary>Couleur représentative d'un thème (pastille du sélecteur).</summary>
    public static Color Swatch(Theme t) => t.Glow ?? Color.FromRgb(
        (byte)Math.Min(255, t.Bg.R + 40), (byte)Math.Min(255, t.Bg.G + 44), (byte)Math.Min(255, t.Bg.B + 52));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
