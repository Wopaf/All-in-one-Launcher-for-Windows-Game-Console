using MaRogAlly.Models;

namespace MaRogAlly.Scanners;

public sealed class RockstarScanner : GameScanner
{
    public override Platform Platform => Platform.Rockstar;

    private static IEnumerable<Reg.UninstallEntry> Entries()
        => Reg.UninstallEntries().Where(e => e.Publisher?.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsTool(string name)
        => name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || name.Contains("Social Club", StringComparison.OrdinalIgnoreCase);

    public override bool IsClientInstalled() => Entries().Any(e => IsTool(e.DisplayName));

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        foreach (var e in Entries())
        {
            if (IsTool(e.DisplayName) || e.InstallLocation is null || !Directory.Exists(e.InstallLocation)) continue;
            yield return new Game
            {
                Platform = Platform.Rockstar,
                PlatformId = e.KeyName,
                Title = e.DisplayName,
                IsInstalled = true,
                InstallDir = e.InstallLocation,
                LaunchExe = Reg.ExeFromIcon(e.DisplayIcon),
            };
        }
    }
}
