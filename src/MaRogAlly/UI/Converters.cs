using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MaRogAlly.UI;

/// <summary>Visible si la valeur est non nulle / non vide (ConverterParameter="invert" pour l'inverse).</summary>
public sealed class VisibleIfConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Couleur « #RRGGBB » vers pinceau, avec opacité optionnelle (ConverterParameter="0.3").</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Brushes.Transparent;
        var c = (Color)ColorConverter.ConvertFromString(s);
        if (parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
            c.A = (byte)(a * 255);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Arrondit les coins d'un élément en le découpant (les Border n'arrondissent pas leur contenu).</summary>
public static class Rounded
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Rounded), new PropertyMetadata(0.0, OnChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
    }
}

/// <summary>Options du style RowButton : <c>NoBar</c> retire le trait coloré de la ligne sélectionnée.</summary>
public static class RowOptions
{
    public static readonly DependencyProperty NoBarProperty = DependencyProperty.RegisterAttached(
        "NoBar", typeof(bool), typeof(RowOptions), new PropertyMetadata(false));
    public static bool GetNoBar(DependencyObject o) => (bool)o.GetValue(NoBarProperty);
    public static void SetNoBar(DependencyObject o, bool v) => o.SetValue(NoBarProperty, v);

    /// <summary>Ligne active (ex. catégorie affichée) : légèrement surlignée même sans le focus.</summary>
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(RowOptions), new PropertyMetadata(false));
    public static bool GetActive(DependencyObject o) => (bool)o.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject o, bool v) => o.SetValue(ActiveProperty, v);

    /// <summary>Sélection dessinée par un <see cref="SlideHighlight"/> commun à la liste, qui glisse d'une ligne à l'autre.</summary>
    public static readonly DependencyProperty FloatingProperty = DependencyProperty.RegisterAttached(
        "Floating", typeof(bool), typeof(RowOptions), new PropertyMetadata(false));
    public static bool GetFloating(DependencyObject o) => (bool)o.GetValue(FloatingProperty);
    public static void SetFloating(DependencyObject o, bool v) => o.SetValue(FloatingProperty, v);

    /// <summary>Ligne posée sur une carte arrondie, comme dans le menu principal (sous un cadre de sélection blanc).</summary>
    public static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
        "Card", typeof(bool), typeof(RowOptions), new PropertyMetadata(false));
    public static bool GetCard(DependencyObject o) => (bool)o.GetValue(CardProperty);
    public static void SetCard(DependencyObject o, bool v) => o.SetValue(CardProperty, v);
}

/// <summary>Rayon égal à la moitié de la hauteur : une pilule reste parfaitement arrondie quelle que soit sa taille.</summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => new CornerRadius(value is double h && h > 0 ? h / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
