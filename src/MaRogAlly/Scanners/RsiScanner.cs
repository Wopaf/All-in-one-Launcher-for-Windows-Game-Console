using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

/// <summary>RSI Launcher (Star Citizen) : le lanceur se trouve par son désinstalleur, le jeu dans son dossier d'installation.</summary>
public sealed class RsiScanner : GameScanner
{
    public override Platform Platform => Platform.Rsi;

    /// <summary>Exécutable du lanceur, d'après son entrée de désinstallation.</summary>
    public static string? LauncherExe()
    {
        foreach (var (hive, root) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var uninstall = hive.OpenSubKey(root);
            foreach (var id in uninstall?.GetSubKeyNames() ?? [])
            {
                using var app = uninstall!.OpenSubKey(id);
                if (app?.GetValue("DisplayName") is not string name || !name.StartsWith("RSI Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                var folder = app.GetValue("InstallLocation") as string;
                if (string.IsNullOrEmpty(folder) && app.GetValue("DisplayIcon") is string icon) folder = Path.GetDirectoryName(icon.Trim('"').Split(',')[0]);
                if (folder is not null && Path.Combine(folder, "RSI Launcher.exe") is var exe && File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    public override bool IsClientInstalled() => LauncherExe() is not null;

    /// <summary>Dossier du jeu : l'emplacement par défaut, ou à côté du lanceur.</summary>
    private static IEnumerable<string> GameFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roberts Space Industries", "StarCitizen");
        if (LauncherExe() is { } exe && Directory.GetParent(Path.GetDirectoryName(exe)!) is { } parent)
        {
            yield return Path.Combine(parent.FullName, "StarCitizen");
            yield return Path.Combine(parent.FullName, "Roberts Space Industries", "StarCitizen");
        }
    }

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var launcher = LauncherExe();
        if (launcher is null) yield break;
        var live = GameFolders().Select(f => Path.Combine(f, "LIVE")).FirstOrDefault(Directory.Exists);
        if (live is null) yield break;
        yield return new Game
        {
            Platform = Platform.Rsi,
            PlatformId = "star-citizen",
            Title = "Star Citizen",
            IsInstalled = true,
            InstallDir = live,
            // Le jeu se lance depuis son lanceur (connexion et mises à jour).
            LaunchExe = launcher,
        };
    }
}
