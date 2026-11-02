using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

public sealed class GogScanner : GameScanner
{
    public override Platform Platform => Platform.Gog;

    private static string? GalaxyClient()
    {
        var dir = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\GalaxyClient\paths", "client");
        var exe = dir is null ? null : Path.Combine(dir, "GalaxyClient.exe");
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    public override bool IsClientInstalled() => GalaxyClient() is not null || File.Exists(GalaxyLibrary.DbPath);

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var galaxy = GalaxyClient();
        foreach (var (key, v) in Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\Games"))
        {
            if (!string.IsNullOrWhiteSpace(v.GetValueOrDefault("dependsOn"))) continue; // DLC
            var id = v.GetValueOrDefault("gameID") ?? key;
            var name = v.GetValueOrDefault("gameName");
            var path = v.GetValueOrDefault("path");
            var exe = v.GetValueOrDefault("exe");
            if (name is null || path is null || !Directory.Exists(path)) continue;

            var game = new Game
            {
                Platform = Platform.Gog,
                PlatformId = id,
                Title = name,
                IsInstalled = true,
                InstallDir = path,
            };
            if (galaxy is not null)
            {
                // Passer par Galaxy active la synchro des sauvegardes et le suivi du temps de jeu.
                game.LaunchExe = galaxy;
                game.LaunchArgs = $"/command=runGame /gameId={id} /path=\"{path}\"";
            }
            else
            {
                game.LaunchExe = exe;
                game.LaunchArgs = v.GetValueOrDefault("launchParam");
            }
            yield return game;
        }
    }

    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        var owned = OwnedFromGalaxy(ctx);
        foreach (var g in owned) g.InstallUri = $"goggalaxy://openGameView/{g.PlatformId}";
        return Task.FromResult(owned);
    }
}
