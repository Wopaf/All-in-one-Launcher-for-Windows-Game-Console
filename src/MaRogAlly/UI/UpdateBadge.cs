using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>
/// Pastille de notification des mises à jour : orange jusqu'à 3 en attente, rouge à partir de 4, masquée à 0.
/// </summary>
public sealed class UpdateBadge : Border
{
    public static readonly Color Few = Color.FromRgb(0xFF, 0x9F, 0x1C);
    public static readonly Color Many = Color.FromRgb(0xFF, 0x3B, 0x4E);

    /// <summary>Couleur selon le nombre de mises à jour en attente.</summary>
    public static Color ColorFor(int count) => count >= 4 ? Many : Few;

    private readonly TextBlock _text = new()
    {
        FontWeight = FontWeights.Bold,
        FontSize = 11,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public UpdateBadge()
    {
        MinWidth = 18;
        Height = 18;
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(5, 0, 5, 0);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _text.FontFamily = (FontFamily)Application.Current.FindResource("Display");
        Child = _text;
    }

    /// <summary>Sans couleur d'alerte : pastille claire et translucide (onglet Réglages).</summary>
    public bool Neutral { get; init; }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
            _text.Text = value > 9 ? "9+" : value.ToString();
            Background = Neutral ? new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(ColorFor(value));
        }
    }
}
