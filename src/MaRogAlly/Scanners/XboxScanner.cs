using System.Text.RegularExpressions;
using MaRogAlly.Core;
using MaRogAlly.Models;
using Windows.Management.Deployment;

namespace MaRogAlly.Scanners;

/// <summary>Jeux Xbox / Microsoft Store / Game Pass : paquets qui contiennent un MicrosoftGame.config.</summary>
public sealed partial class XboxScanner : GameScanner
{
    public override Platform Platform => Platform.Xbox;

    public override bool IsClientInstalled()
    {
        try
        {
            return new PackageManager().FindPackagesForUser("")
                .Any(p => p.Id.Name.StartsWith("Microsoft.GamingApp", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var games = new List<Game>();
        try
        {
            foreach (var pkg in new PackageManager().FindPackagesForUser(""))
            {
                try
                {
                    if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                    // Le lanceur Minecraft a sa propre plateforme.
                    if (pkg.Id.Name.StartsWith(MinecraftScanner.PackagePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = pkg.InstalledLocation?.Path;
                    if (dir is null) continue;
                    var config = Path.Combine(dir, "MicrosoftGame.config");
                    if (!File.Exists(config)) continue;

                    var appId = ApplicationId(Path.Combine(dir, "AppxManifest.xml")) ?? "Game";
                    var title = pkg.DisplayName;
                    if (string.IsNullOrWhiteSpace(title) || title.StartsWith("ms-resource:")) title = pkg.Id.Name;

                    games.Add(new Game
                    {
                        Platform = Platform.Xbox,
                        PlatformId = pkg.Id.FamilyName,
                        Title = title,
                        IsInstalled = true,
                        InstallDir = dir,
                        LaunchExe = "explorer.exe",
                        LaunchArgs = $"shell:AppsFolder\\{pkg.Id.FamilyName}!{appId}",
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"Paquet ignoré ({pkg.Id.FullName})", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Liste des paquets Windows inaccessible", ex);
        }
        return games;
    }

    private static string? ApplicationId(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        var m = AppIdRegex().Match(File.ReadAllText(manifest));
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<Application\s[^>]*?Id=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();
}
