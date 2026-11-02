using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

public sealed class UbisoftScanner : GameScanner
{
    public override Platform Platform => Platform.Ubisoft;

    private const string LauncherKey = @"SOFTWARE\Ubisoft\Launcher";

    public override bool IsClientInstalled() => Reg.Get(RegistryHive.LocalMachine, LauncherKey, "InstallDir") is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var names = Reg.UninstallEntries()
            .Where(e => e.KeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.KeyName["Uplay Install ".Length..])
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        foreach (var (id, v) in Reg.SubKeys(RegistryHive.LocalMachine, LauncherKey + @"\Installs"))
        {
            var dir = v.GetValueOrDefault("InstallDir")?.Replace('/', '\\').TrimEnd('\\');
            if (dir is null || !Directory.Exists(dir)) continue;
            yield return new Game
            {
                Platform = Platform.Ubisoft,
                PlatformId = id,
                Title = names.GetValueOrDefault(id) ?? Path.GetFileName(dir),
                IsInstalled = true,
                InstallDir = dir,
                LaunchUri = $"uplay://launch/{id}/0",
            };
        }
    }
}
