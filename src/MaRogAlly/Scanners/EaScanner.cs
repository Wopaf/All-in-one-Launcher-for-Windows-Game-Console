using System.Text.RegularExpressions;
using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

public sealed partial class EaScanner : GameScanner
{
    public override Platform Platform => Platform.Ea;

    public override bool IsClientInstalled()
        => Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation") is not null
        || Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Classes\origin2", "") is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        foreach (var e in Reg.UninstallEntries())
        {
            // Les jeux de l'EA app se désinstallent via EAInstaller\…\Cleanup.exe.
            if (e.UninstallString?.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (e.InstallLocation is null || !Directory.Exists(e.InstallLocation)) continue;

            var game = new Game
            {
                Platform = Platform.Ea,
                PlatformId = e.KeyName,
                Title = e.DisplayName,
                IsInstalled = true,
                InstallDir = e.InstallLocation,
            };

            var offers = ContentIds(e.InstallLocation);
            if (offers.Count > 0) game.LaunchUri = $"origin2://game/launch?offerIds={string.Join(",", offers)}&autoDownload=1";
            else game.LaunchExe = Reg.ExeFromIcon(e.DisplayIcon);
            yield return game;
        }
    }

    /// <summary>Identifiants d'offre EA lus dans __Installer\installerdata.xml.</summary>
    private static List<string> ContentIds(string installDir)
    {
        var xml = Path.Combine(installDir, "__Installer", "installerdata.xml");
        if (!File.Exists(xml)) return [];
        try
        {
            return ContentIdRegex().Matches(File.ReadAllText(xml))
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
        }
        catch { return []; }
    }

    [GeneratedRegex(@"<contentID>([^<]+)</contentID>", RegexOptions.IgnoreCase)]
    private static partial Regex ContentIdRegex();
}
