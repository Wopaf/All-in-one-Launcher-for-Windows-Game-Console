using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Scanners;

public sealed class BattleNetScanner : GameScanner
{
    public override Platform Platform => Platform.BattleNet;

    // Codes produit utilisés par « Battle.net.exe --exec="launch CODE" ».
    private static readonly (string Match, string Code)[] Codes =
    [
        ("world of warcraft", "WoW"),
        ("overwatch", "Pro"),
        ("diablo iv", "Fen"),
        ("diablo ii resurrected", "OSI"),
        ("diablo iii", "D3"),
        ("diablo immortal", "ANBS"),
        ("hearthstone", "WTCG"),
        ("heroes of the storm", "Hero"),
        ("starcraft ii", "S2"),
        ("starcraft", "S1"),
        ("warcraft iii", "W3"),
        ("call of duty black ops cold war", "ZEUS"),
        ("call of duty vanguard", "FORE"),
        ("call of duty modern warfare", "ODIN"),
        ("call of duty", "AUKS"),
        ("crash bandicoot 4", "WLBY"),
    ];

    private static string? ClientExe()
    {
        var entry = Reg.UninstallEntries().FirstOrDefault(e => e.DisplayName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase));
        var candidates = new[]
        {
            entry?.InstallLocation is { } d ? Path.Combine(d, "Battle.net.exe") : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Battle.net\Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Battle.net\Battle.net.exe"),
        };
        return candidates.FirstOrDefault(p => p is not null && File.Exists(p));
    }

    public override bool IsClientInstalled() => ClientExe() is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var client = ClientExe();
        foreach (var e in Reg.UninstallEntries())
        {
            if (e.Publisher?.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (e.DisplayName.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.InstallLocation is null || !Directory.Exists(e.InstallLocation)) continue;

            var norm = Text.Normalize(e.DisplayName);
            var code = Codes.FirstOrDefault(c => norm.StartsWith(c.Match)).Code;
            var game = new Game
            {
                Platform = Platform.BattleNet,
                PlatformId = code ?? e.KeyName,
                Title = e.DisplayName,
                IsInstalled = true,
                InstallDir = e.InstallLocation,
            };
            if (code is not null && client is not null)
            {
                game.LaunchExe = client;
                game.LaunchArgs = $"--exec=\"launch {code}\"";
            }
            else
            {
                game.LaunchExe = Reg.ExeFromIcon(e.DisplayIcon) ?? client;
            }
            yield return game;
        }
    }
}
