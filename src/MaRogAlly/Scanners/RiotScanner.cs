using System.Text.Json;
using System.Text.RegularExpressions;
using MaRogAlly.Models;

namespace MaRogAlly.Scanners;

public sealed partial class RiotScanner : GameScanner
{
    public override Platform Platform => Platform.Riot;

    private static readonly Dictionary<string, string> Names = new()
    {
        ["league_of_legends"] = "League of Legends",
        ["valorant"] = "VALORANT",
        ["bacon"] = "Legends of Runeterra",
        ["lion"] = "2XKO",
    };

    private static string RiotData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");

    public static string? ClientServices()
    {
        var installs = Path.Combine(RiotData, "RiotClientInstalls.json");
        if (!File.Exists(installs)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(installs));
            foreach (var key in new[] { "rc_default", "rc_live" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.GetString() is { } p && File.Exists(p))
                    return Path.GetFullPath(p);
        }
        catch { }
        return null;
    }

    public override bool IsClientInstalled() => ClientServices() is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var client = ClientServices();
        var meta = Path.Combine(RiotData, "Metadata");
        if (client is null || !Directory.Exists(meta)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(meta))
        {
            // Dossiers de la forme « valorant.live ».
            var name = Path.GetFileName(dir);
            var dot = name.LastIndexOf('.');
            if (dot <= 0) continue;
            var product = name[..dot];
            var patchline = name[(dot + 1)..];
            if (product == "riot_client") continue;

            var settings = Path.Combine(dir, $"{name}.product_settings.yaml");
            if (!File.Exists(settings)) continue;
            var m = InstallPathRegex().Match(File.ReadAllText(settings));
            if (!m.Success) continue;
            var installDir = m.Groups[1].Value.Replace('/', '\\');
            if (!Directory.Exists(installDir)) continue;

            yield return new Game
            {
                Platform = Platform.Riot,
                PlatformId = product,
                Title = Names.GetValueOrDefault(product) ?? product.Replace('_', ' '),
                IsInstalled = true,
                InstallDir = installDir,
                LaunchExe = client,
                LaunchArgs = $"--launch-product={product} --launch-patchline={patchline}",
            };
        }
    }

    [GeneratedRegex(@"product_install_full_path:\s*""?([^""\r\n]+)""?")]
    private static partial Regex InstallPathRegex();
}
