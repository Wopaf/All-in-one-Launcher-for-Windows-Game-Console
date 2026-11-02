using MaRogAlly.Core;
using MaRogAlly.Models;
using Windows.Management.Deployment;

namespace MaRogAlly.Scanners;

/// <summary>Minecraft Launcher : application du Store ou version classique, avec les versions de jeu téléchargées dans .minecraft.</summary>
public sealed class MinecraftScanner : GameScanner
{
    /// <summary>Paquet du lanceur dans le Microsoft Store (écarté des jeux Xbox, pour ne pas l'avoir en double).</summary>
    public const string PackagePrefix = "Microsoft.4297127D64EC6";

    public override Platform Platform => Platform.Minecraft;

    private static string ClassicExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe");

    /// <summary>De quoi ouvrir le lanceur (programme ou adresse d'application), ou null s'il n'est pas installé.</summary>
    public static string? LauncherTarget()
    {
        if (File.Exists(ClassicExe)) return ClassicExe;
        try
        {
            var package = new PackageManager().FindPackagesForUser("")
                .FirstOrDefault(p => p.Id.Name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase));
            return package is null ? null : $@"shell:AppsFolder\{package.Id.FamilyName}!Minecraft";
        }
        catch (Exception ex)
        {
            Log.Error("Recherche du lanceur Minecraft", ex);
            return null;
        }
    }

    public override bool IsClientInstalled() => LauncherTarget() is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        if (LauncherTarget() is not { } target) yield break;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        var isApp = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        yield return new Game
        {
            Platform = Platform.Minecraft,
            PlatformId = "minecraft",
            Title = "Minecraft",
            IsInstalled = true,
            InstallDir = Directory.Exists(data) ? data : null,
            LaunchExe = isApp ? "explorer.exe" : target,
            LaunchArgs = isApp ? target : null,
        };
    }
}
