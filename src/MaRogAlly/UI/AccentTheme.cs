using System.Windows;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>
/// Couleur d'accent de l'interface. Les ressources « Accent… » sont utilisées en DynamicResource :
/// les remplacer ici repeint toute l'application instantanément.
/// </summary>
public static class AccentTheme
{
    public const string Default = "#FF2E4D";

    public static readonly IReadOnlyList<(string Name, string Hex)> Choices =
    [
        ("Rouge ROG", "#FF2E4D"),
        ("Corail", "#FF6B5B"),
        ("Orange", "#FF8A1F"),
        ("Ambre", "#FFB627"),
        ("Vert", "#2FD07A"),
        ("Menthe", "#2DD4BF"),
        ("Cyan", "#22B8F0"),
        ("Bleu", "#3B82F6"),
        ("Indigo", "#6D6BF5"),
        ("Violet", "#A35BF7"),
        ("Rose", "#F2499B"),
        ("Argent", "#B8C2D1"),
    ];

    public static Color Parse(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? Default : hex); }
        catch { return (Color)ColorConverter.ConvertFromString(Default); }
    }

    public static void Apply(string? hex)
    {
        var c = Parse(hex);
        var res = Application.Current.Resources;
        res["AccentColor"] = c;
        res["AccentGlowColor"] = Color.FromArgb(0x55, c.R, c.G, c.B);
        res["AccentClearColor"] = Color.FromArgb(0x00, c.R, c.G, c.B);
        res["Accent"] = Frozen(c);
        res["AccentSoft"] = Frozen(Color.FromArgb(0x55, c.R, c.G, c.B));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
