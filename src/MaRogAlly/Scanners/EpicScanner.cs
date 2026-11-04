using System.Text;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

public sealed class EpicScanner : GameScanner
{
    public override Platform Platform => Platform.Epic;

    private static string ManifestsDir()
    {
        var data = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath");
        return data is not null
            ? Path.Combine(data, "Manifests")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
    }

    /// <summary>
    /// Cache du catalogue (bibliothèque possédée). Les versions récentes du lanceur le rangent dans ProgramData,
    /// les anciennes dans le profil : on prend le plus récent des deux.
    /// </summary>
    private static string CatalogCache => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Catalog\catcache.bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"EpicGamesLauncher\Saved\Data\Catalog\catcache.bin"),
        }
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault() ?? "";

    public override bool IsClientInstalled()
        => Directory.Exists(ManifestsDir())
        || Reg.Get(RegistryHive.CurrentUser, @"Software\Classes\com.epicgames.launcher", "") is not null
        || Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Classes\com.epicgames.launcher", "") is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var dir = ManifestsDir();
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            Game? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var appName = Str(r, "AppName");
                var main = Str(r, "MainGameAppName");
                // DLC : rattaché à un autre jeu. Les jeux de base ont un MainGameAppName vide ou égal à leur AppName.
                if (appName is null || (!string.IsNullOrEmpty(main) && main != appName)) continue;
                if (Str(r, "DisplayName") is { } dn && (dn.Contains("(DLC)", StringComparison.OrdinalIgnoreCase) || IsTool(dn))) continue;
                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    && !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;

                var ns = Str(r, "CatalogNamespace");
                var item = Str(r, "CatalogItemId");
                game = new Game
                {
                    Platform = Platform.Epic,
                    PlatformId = appName,
                    Title = Str(r, "DisplayName") ?? appName,
                    IsInstalled = true,
                    InstallDir = Str(r, "InstallLocation"),
                    LaunchUri = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{appName}?action=launch&silent=true",
                };
            }
            catch (Exception ex)
            {
                Log.Error($"Manifeste Epic illisible ({file})", ex);
            }
            if (game is not null) yield return game;
        }
    }

    /// <summary>Le lanceur Epic garde en cache (base64 + JSON) le catalogue des jeux du compte.</summary>
    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        var list = new List<Game>();
        if (!File.Exists(CatalogCache)) return Task.FromResult<IReadOnlyList<Game>>(OwnedFromGalaxy(ctx));
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(CatalogCache).Trim()));
            using var doc = JsonDocument.Parse(json);
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                // DLC : rattaché à un jeu principal. Les versions récentes du lanceur écrivent un
                // mainGameItem vide ({"id": ""}) pour les jeux de base, d'où le test sur l'identifiant.
                if (it.TryGetProperty("mainGameItem", out var mg) && mg.ValueKind == JsonValueKind.Object
                    && !string.IsNullOrEmpty(Str(mg, "id"))) continue;
                // Les jeux portent désormais aussi la catégorie « applications » : seuls « games » et « addons » comptent.
                if (!HasCategory(it, "games") || HasCategory(it, "addons")) continue;

                var appId = it.TryGetProperty("releaseInfo", out var rel) && rel.ValueKind == JsonValueKind.Array
                    ? rel.EnumerateArray().Select(x => Str(x, "appId")).FirstOrDefault(x => x is not null)
                    : null;
                var title = Str(it, "title");
                if (appId is null || title is null || IsTool(title)) continue;

                var ns = Str(it, "namespace");
                var id = Str(it, "id");
                list.Add(new Game
                {
                    Platform = Platform.Epic,
                    PlatformId = appId,
                    Title = title,
                    InstallUri = $"com.epicgames.launcher://apps/{ns}%3A{id}%3A{appId}?action=install",
                    CoverUrl = Image(it, "DieselGameBoxTall"),
                    HeroUrl = Image(it, "DieselGameBox"),
                    LogoUrl = Image(it, "DieselGameBoxLogo"),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Cache du catalogue Epic illisible", ex);
        }
        return Task.FromResult<IReadOnlyList<Game>>(list.DistinctBy(g => g.PlatformId).ToList());
    }

    // Applications Epic (moteur, outils 3D) qui ne sont pas des jeux.
    private static readonly string[] Tools = ["Twinmotion", "Unreal Engine", "MetaHuman", "RealityCapture", "Quixel Bridge", "Unreal Editor"];

    private static bool IsTool(string title) => Tools.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static bool HasCategory(JsonElement it, string path)
        => it.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
           && cats.EnumerateArray().Any(c => Str(c, "path") == path);

    private static string? Image(JsonElement it, string type)
        => it.TryGetProperty("keyImages", out var imgs) && imgs.ValueKind == JsonValueKind.Array
            ? imgs.EnumerateArray().Where(i => Str(i, "type") == type).Select(i => Str(i, "url")).FirstOrDefault()
            : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
