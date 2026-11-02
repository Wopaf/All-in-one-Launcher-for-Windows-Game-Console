using System.IO;

namespace MaRogAlly.Core;

/// <summary>Emplacements des données de l'application (%LOCALAPPDATA%\MaRogAlly).</summary>
public static class AppPaths
{
    /// <summary>« --data dossier » : autre dossier de données, pour essayer sans toucher aux vraies.</summary>
    public static string Root { get; } = (Array.IndexOf(Environment.GetCommandLineArgs(), "--data") is var i and >= 0 ? Environment.GetCommandLineArgs().ElementAtOrDefault(i + 1) : null)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaRogAlly");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Library => Path.Combine(Root, "library.json");
    public static string UserData => Path.Combine(Root, "userdata.json");
    public static string MetadataDir => Path.Combine(Root, "metadata");
    public static string ImagesDir => Path.Combine(Root, "images");
    public static string LogFile => Path.Combine(Root, "log.txt");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MetadataDir);
        Directory.CreateDirectory(ImagesDir);
    }
}
