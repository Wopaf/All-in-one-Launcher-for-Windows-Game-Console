using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>Format commun des cartes de jeu (jaquettes), sur tous les onglets.</summary>
public static class CoverArt
{
    /// <summary>
    /// Hauteur d'une carte pour une largeur de 1. Entre le 2:3 des jaquettes Steam (1,5) et le 3:4 de celles d'IGDB (1,33) :
    /// les unes comme les autres ne perdent que quelques pour cent sur les bords.
    /// </summary>
    public const double Ratio = 1.4;

    /// <summary>Jaquette qui remplit sa carte, recadrée au centre (et non depuis le coin haut gauche).</summary>
    public static Image Image() => new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
