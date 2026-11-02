using System.Diagnostics;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

public static class GameLauncher
{
    public static bool Launch(Game game)
    {
        if (!game.IsInstalled) return Install(game);
        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchUri)) return Open(game.LaunchUri);
            if (!string.IsNullOrWhiteSpace(game.LaunchExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.LaunchExe,
                    Arguments = game.LaunchArgs ?? "",
                    WorkingDirectory = File.Exists(game.LaunchExe) ? Path.GetDirectoryName(game.LaunchExe)! : "",
                    UseShellExecute = true,
                });
                return true;
            }
            if (game.InstallDir is not null && Directory.Exists(game.InstallDir)) return Open(game.InstallDir);
        }
        catch (Exception ex)
        {
            Log.Error($"Lancement impossible : {game.Title}", ex);
        }
        return false;
    }

    /// <summary>Ouvre la page d'installation dans le lanceur, ou la page de téléchargement du lanceur.</summary>
    public static bool Install(Game game)
        => Open(game.InstallUri ?? game.PlatformInfo.LauncherUri ?? game.PlatformInfo.DownloadUrl);

    public static bool OpenPlatform(PlatformInfo info, bool clientInstalled)
        => Open(clientInstalled && info.LauncherUri is not null ? info.LauncherUri : info.DownloadUrl);

    /// <summary>Adresses de la page du jeu dans sa plateforme (lanceur d'abord, puis site web en secours).</summary>
    private static IEnumerable<string?> PlatformPages(Game g) => g.Platform switch
    {
        Platform.Steam => [$"steam://nav/games/details/{g.PlatformId}", $"https://store.steampowered.com/app/{g.PlatformId}"],
        Platform.Gog => [$"goggalaxy://openGameView/{g.PlatformId}", g.PlatformInfo.DownloadUrl],
        Platform.Epic => ["com.epicgames.launcher://library", "https://store.epicgames.com/"],
        Platform.Ea => [g.PlatformInfo.LauncherUri, g.PlatformInfo.DownloadUrl],
        Platform.Ubisoft => ["uplay://open/library", g.PlatformInfo.LauncherUri, g.PlatformInfo.DownloadUrl],
        Platform.BattleNet => [$"battlenet://{g.PlatformId}", g.PlatformInfo.DownloadUrl],
        Platform.Xbox => [$"ms-windows-store://pdp/?PFN={g.PlatformId}", g.PlatformInfo.LauncherUri],
        Platform.Amazon => [g.PlatformInfo.LauncherUri, g.PlatformInfo.DownloadUrl],
        Platform.Itch => [$"itch://games/{g.PlatformId}", "https://itch.io/my-collections"],
        _ => [],
    };

    public static bool HasPlatformPage(Game game) => PlatformPages(game).Any(u => u is not null);

    /// <summary>Ouvre le jeu dans la bibliothèque de sa plateforme (Steam, GOG Galaxy, Epic…).</summary>
    public static bool OpenInPlatform(Game game)
        => PlatformPages(game).Any(u => u is not null && HandlerExists(u) && Open(u));

    /// <summary>
    /// Windows n'échoue pas sur un protocole inconnu (il propose de chercher une application) :
    /// on vérifie donc que le lanceur a bien enregistré son protocole avant de l'utiliser.
    /// </summary>
    private static bool HandlerExists(string uri)
    {
        var scheme = uri.Split(':')[0];
        if (scheme is "http" or "https") return true;
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
        return key is not null;
    }

    /// <summary>
    /// Lance la désinstallation : par Steam, sinon par le programme de désinstallation déclaré à Windows,
    /// sinon dans le lanceur (Epic) ou dans « Applications installées ». Le désinstalleur demande lui-même confirmation.
    /// </summary>
    public static bool Uninstall(Game game)
    {
        if (game.Platform == Platform.Steam) return Open($"steam://uninstall/{game.PlatformId}");
        if (FindUninstallCommand(game) is { } cmd && RunCommand(cmd)) return true;
        if (game.Platform == Platform.Epic && HandlerExists("com.epicgames.launcher:")) return Open("com.epicgames.launcher://library");
        return Open("ms-settings:appsfeatures");
    }

    /// <summary>Entrée « Applications installées » du jeu : même dossier d'installation, sinon même nom.</summary>
    private static string? FindUninstallCommand(Game game)
    {
        static string Dir(string? p) => (p ?? "").Trim('"', ' ').TrimEnd('\\', '/');
        var dir = Dir(game.InstallDir);
        var title = Text.Normalize(game.Title);
        var entries = Scanners.Reg.UninstallEntries().Where(e => !string.IsNullOrWhiteSpace(e.UninstallString)).ToList();
        var match = entries.FirstOrDefault(e => dir.Length > 3 && string.Equals(Dir(e.InstallLocation), dir, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(e => Text.Normalize(e.DisplayName) == title);
        return match?.UninstallString;
    }

    /// <summary>Exécute une ligne de commande de désinstallation (« "C:\x\unins000.exe" /args » ou « MsiExec.exe /X{…} »).</summary>
    private static bool RunCommand(string command)
    {
        try
        {
            command = command.Trim();
            string exe, args;
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                exe = command[1..end];
                args = command[(end + 1)..].Trim();
            }
            else
            {
                var i = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exe = i > 0 ? command[..(i + 4)] : command;
                args = i > 0 ? command[(i + 4)..].Trim() : "";
            }
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return true; // demande d'élévation refusée par l'utilisateur
        }
        catch (Exception ex)
        {
            Log.Error($"Désinstallation impossible : {command}", ex);
            return false;
        }
    }

    public static bool OpenFolder(Game game)
        => game.InstallDir is not null && Directory.Exists(game.InstallDir) && Open(game.InstallDir);

    public static bool Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Ouverture impossible : {target}", ex);
            return false;
        }
    }
}
