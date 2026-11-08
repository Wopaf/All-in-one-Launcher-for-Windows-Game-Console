using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace MaRogAlly.Core;

/// <summary>Dimensions des visuels en cache, lues dans l'en-tête du fichier (sans décoder l'image).</summary>
public static class Images
{
    private static readonly ConcurrentDictionary<string, bool> Wide = new();

    /// <summary>Image plus large que haute (capsule Steam, en-tête) : pas une jaquette verticale.</summary>
    public static bool IsWide(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return Wide.GetOrAdd(key, _ =>
            {
                using var stream = File.OpenRead(path);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                return frame.PixelWidth > frame.PixelHeight;
            });
        }
        catch (Exception)
        {
            return false;
        }
    }
}
