using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>Captures de l'écran principal, enregistrées dans Images\Ma ROG Ally. La galerie montre aussi celles de Windows (Win+Impr. écran).</summary>
public static class ScreenCapture
{
    private const uint CopyWithLayers = 0x00CC0020 | 0x40000000;
    private const int ScreenWidth = 0, ScreenHeight = 1;

    private static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private static string Folder => Path.Combine(Pictures, "Ma ROG Ally");

    /// <summary>Chemin du fichier créé, ou null en cas d'échec. Un jeu en plein écran exclusif peut donner une image noire.</summary>
    public static async Task<string?> SaveAsync()
    {
        try
        {
            var frame = Grab();
            frame.Freeze();
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, $"Capture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(frame));
                using var file = File.Create(path);
                png.Save(file);
                return path;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Capture d'écran", ex);
            return null;
        }
    }

    /// <summary>Captures les plus récentes d'abord.</summary>
    public static List<string> List(int limit)
    {
        var files = new List<FileInfo>();
        foreach (var folder in new[] { Folder, Path.Combine(Pictures, "Screenshots") })
        {
            try
            {
                if (Directory.Exists(folder))
                    files.AddRange(new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Lecture des captures ({folder})", ex);
            }
        }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).Select(f => f.FullName).ToList();
    }

    /// <summary>Image réduite à cette largeur : à lire hors du fil de l'interface. Null si le fichier est illisible.</summary>
    public static BitmapImage? Load(string path, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            Log.Error($"Capture illisible ({path})", ex);
            return null;
        }
    }

    private static BitmapSource Grab()
    {
        int width = GetSystemMetrics(ScreenWidth), height = GetSystemMetrics(ScreenHeight);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, 0, 0, CopyWithLayers);
            return Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
