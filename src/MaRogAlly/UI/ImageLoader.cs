using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MaRogAlly.UI;

/// <summary>
/// Chargement asynchrone des images depuis le disque, redimensionnées au décodage (moins de mémoire,
/// défilement fluide) et affichées avec un fondu. Usage : ui:ImageLoader.Source="{Binding CoverPath}".
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>Largeur de décodage en pixels (0 = taille d'origine).</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(0));

    public static string? GetSource(DependencyObject o) => (string?)o.GetValue(SourceProperty);
    public static void SetSource(DependencyObject o, string? v) => o.SetValue(SourceProperty, v);
    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject o, int v) => o.SetValue(DecodeWidthProperty, v);

    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> Cache = new();
    private static readonly SemaphoreSlim Decoders = new(Math.Max(2, Environment.ProcessorCount / 2));

    private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var path = e.NewValue as string;
        if (string.IsNullOrEmpty(path))
        {
            image.Source = null;
            return;
        }

        var width = GetDecodeWidth(image);
        var key = $"{path}|{width}";
        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
        {
            image.Source = cached;
            image.Opacity = 1;
            return;
        }

        image.Opacity = 0;
        var bitmap = await Task.Run(() => Decode(path, width));
        if (GetSource(image) != path) return; // une autre image a été demandée entre-temps
        image.Source = bitmap;
        if (bitmap is null) return;
        Cache[key] = new WeakReference<BitmapSource>(bitmap);
        image.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuadraticEase() });
    }

    /// <summary>Décode une image hors du thread d'interface (pour les fonds en fondu enchaîné).</summary>
    public static Task<BitmapSource?> LoadAsync(string path, int width) => Task.Run(() => Decode(path, width));

    private static BitmapSource? Decode(string path, int width)
    {
        Decoders.Wait();
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bmp.DecodePixelWidth = width;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            Decoders.Release();
        }
    }
}
