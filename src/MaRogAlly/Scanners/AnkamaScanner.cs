using System.Text.Json;
using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

/// <summary>
/// Ankama Launcher (Dofus, Wakfu, Waven…) : chaque jeu installé a un fichier release.json
/// dans %APPDATA%\zaap\repositories\production\{jeu}\{version}, avec son dossier d'installation.
/// </summary>
public sealed class AnkamaScanner : GameScanner
{
    public override Platform Platform => Platform.Ankama;

    private static string Repositories => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zaap", "repositories", "production");

    /// <summary>Noms affichés ; les versions de test (beta, experimental…) sont ignorées.</summary>
    private static readonly Dictionary<string, string> Names = new()
    {
        ["dofus/dofus3"] = "Dofus",
        ["dofus/main"] = "Dofus 2",
        ["retro/main"] = "Dofus Retro",
        ["wakfu/main"] = "Wakfu",
        ["waven/main"] = "Waven",
        ["krosmaga/main"] = "Krosmaga",
        ["flyn/main"] = "Fly'n",
        ["onemoregate/main"] = "One More Gate",
        ["supernanoblaster/main"] = "Super Nano Blaster",
        ["krosfighter/main"] = "Kros Fighter",
        ["maliki/main"] = "Maliki",
        ["savara/main"] = "Savara",
    };

    /// <summary>Exécutable du lanceur, d'après son protocole zaap://.</summary>
    private static string? LauncherExe()
    {
        var command = Reg.Get(RegistryHive.CurrentUser, @"Software\Classes\zaap\shell\open\command", "")
                      ?? Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Classes\zaap\shell\open\command", "");
        if (command is null) return null;
        var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
        return File.Exists(exe) ? exe : null;
    }

    public override bool IsClientInstalled() => LauncherExe() is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        if (!Directory.Exists(Repositories)) yield break;
        foreach (var gameDir in Directory.EnumerateDirectories(Repositories))
        foreach (var releaseDir in Directory.EnumerateDirectories(gameDir))
        {
            var key = $"{Path.GetFileName(gameDir)}/{Path.GetFileName(releaseDir)}";
            string? title = Names.GetValueOrDefault(key);
            if (title is null && !key.EndsWith("/main")) continue;
            var file = Path.Combine(releaseDir, "release.json");
            if (!File.Exists(file)) continue;

            string? location, gameUid, name;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                location = r.TryGetProperty("location", out var l) ? l.GetString() : null;
                gameUid = r.TryGetProperty("gameUid", out var u) ? u.GetString() : null;
                name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
                title ??= r.TryGetProperty("gameName", out var g) ? g.GetString() : null;
                if (r.TryGetProperty("isInstalling", out var installing) && installing.ValueKind == JsonValueKind.True) continue;
            }
            catch { continue; }
            if (location is null || gameUid is null || name is null || title is null || !Directory.Exists(location)) continue;

            yield return new Game
            {
                Platform = Platform.Ankama,
                PlatformId = $"{gameUid}-{name}",
                Title = title,
                IsInstalled = true,
                InstallDir = location,
                // Le lanceur ouvre le jeu demandé (et s'occupe de la connexion et des mises à jour).
                LaunchUri = $"zaap://app/{gameUid}/{name}",
            };
        }
    }

    /// <summary>
    /// Jeux déjà joués d'après le journal du lanceur, avec leur temps de jeu (même désinstallés : ils restent dans la Bibliothèque).
    /// Dofus 2 a laissé la place à Dofus 3 : si sa version n'est plus installée, son temps s'ajoute à celui de Dofus.
    /// </summary>
    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        var plays = AnkamaHistory.Read();
        // Son dossier peut subsister sans le jeu : seul le scan dit s'il est encore installé.
        if (plays.TryGetValue("dofus-main", out var dofus2) && !ScanInstalled(ctx).Any(g => g.PlatformId == "dofus-main") && plays.Remove("dofus-main"))
            plays["dofus-dofus3"] = plays.TryGetValue("dofus-dofus3", out var dofus3)
                ? new AnkamaPlay(dofus3.Minutes + dofus2.Minutes, dofus3.LastPlayed > dofus2.LastPlayed ? dofus3.LastPlayed : dofus2.LastPlayed)
                : dofus2;

        var games = new List<Game>();
        foreach (var (key, play) in plays)
        {
            if (key.Split('-') is not [var uid, var release] || !Names.TryGetValue($"{uid}/{release}", out var title)) continue;
            games.Add(new Game
            {
                Platform = Platform.Ankama,
                PlatformId = key,
                Title = title,
                PlaytimeMinutes = play.Minutes,
                PlatformLastPlayed = play.LastPlayed,
                InstallUri = $"zaap://app/{uid}/{release}",
            });
        }
        return Task.FromResult<IReadOnlyList<Game>>(games);
    }
}
