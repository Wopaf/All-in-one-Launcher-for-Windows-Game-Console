using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MaRogAlly.Core;
using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Scanners;

public sealed class SteamScanner : GameScanner
{
    public override Platform Platform => Platform.Steam;

    // Outils Steam qui ne sont pas des jeux.
    private static readonly HashSet<string> ExcludedIds = ["228980", "1070560", "1391110", "1628350", "1493710", "1826330", "1887720", "2180100", "250820", "431960"];
    private static readonly string[] ExcludedNames = ["Proton", "Steam Linux Runtime", "Steamworks", "Redistributable", "SteamVR", "Wallpaper Engine"];

    public static string? SteamPath()
    {
        var p = Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")
             ?? Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        return p is not null && Directory.Exists(p) ? Path.GetFullPath(p) : null;
    }

    public override bool IsClientInstalled() => SteamPath() is not null;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        var steam = SteamPath();
        if (steam is null) yield break;

        foreach (var lib in LibraryFolders(steam))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var state = VdfNode.LoadFile(manifest)?.Child("AppState");
                var id = state?["appid"];
                var name = state?["name"];
                if (id is null || name is null || IsExcluded(id, name)) continue;
                // Première installation en cours : pas encore jouable (une simple mise à jour garde son numéro de version).
                if (int.TryParse(state!["StateFlags"], out var flags) && (flags & 4) == 0 && state["buildid"] is null or "0") continue;
                var dir = state!["installdir"] is { } d ? Path.Combine(apps, "common", d) : null;
                yield return new Game
                {
                    Platform = Platform.Steam,
                    PlatformId = id,
                    Title = name,
                    SteamAppId = int.TryParse(id, out var n) ? n : null,
                    IsInstalled = true,
                    InstallDir = dir,
                    LaunchUri = $"steam://rungameid/{id}",
                };
            }
        }
    }

    public override async Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        var key = ctx.Settings.SteamApiKey;
        var steamId = string.IsNullOrWhiteSpace(ctx.Settings.SteamId) ? DetectSteamId() : ctx.Settings.SteamId;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(steamId))
            return OwnedFromGalaxy(ctx);

        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={key}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json";
        var result = await ctx.Http.GetFromJsonAsync<OwnedResponse>(url, ct);
        return (result?.Response?.Games ?? [])
            .Where(g => !IsExcluded(g.AppId.ToString(), g.Name ?? ""))
            .Select(g => new Game
            {
                Platform = Platform.Steam,
                PlatformId = g.AppId.ToString(),
                Title = g.Name ?? $"App {g.AppId}",
                SteamAppId = g.AppId,
                PlaytimeMinutes = g.PlaytimeForever,
                InstallUri = $"steam://install/{g.AppId}",
            })
            .ToList();
    }

    /// <summary>SteamID64 du dernier compte connecté (config/loginusers.vdf).</summary>
    public static string? DetectSteamId()
    {
        var steam = SteamPath();
        if (steam is null) return null;
        var users = VdfNode.LoadFile(Path.Combine(steam, "config", "loginusers.vdf"))?.Child("users");
        if (users is null) return null;
        return users.Children.FirstOrDefault(u => u.Value["MostRecent"] == "1").Key
            ?? users.Children.Keys.FirstOrDefault();
    }

    /// <summary>
    /// Temps de jeu et dernière partie enregistrés localement par Steam pour chaque compte de la machine
    /// (userdata\&lt;id&gt;\config\localconfig.vdf) : disponible sans clé d'API.
    /// </summary>
    public static Dictionary<string, (int Minutes, DateTime? Last)> LocalPlayTimes()
    {
        var result = new Dictionary<string, (int Minutes, DateTime? Last)>();
        var steam = SteamPath();
        var userdata = steam is null ? null : Path.Combine(steam, "userdata");
        if (userdata is null || !Directory.Exists(userdata)) return result;

        foreach (var user in Directory.EnumerateDirectories(userdata))
        {
            var root = VdfNode.LoadFile(Path.Combine(user, "config", "localconfig.vdf"));
            var apps = root?.Child("UserLocalConfigStore")?.Child("Software")?.Child("Valve")?.Child("Steam")?.Child("apps");
            if (apps is null) continue;
            foreach (var (id, app) in apps.Children)
            {
                var minutes = int.TryParse(app["Playtime"], out var m) ? m : 0;
                DateTime? last = long.TryParse(app["LastPlayed"], out var unix) && unix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
                    : null;
                if (minutes == 0 && last is null) continue;
                var cur = result.GetValueOrDefault(id);
                result[id] = (Math.Max(cur.Minutes, minutes), cur.Last is { } l && last is { } n && l > n ? l : last ?? cur.Last);
            }
        }
        return result;
    }

    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var result = new List<string> { steam };
        var root = VdfNode.LoadFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
        var folders = root?.Child("libraryfolders") ?? root?.Child("LibraryFolders");
        if (folders is not null)
        {
            foreach (var child in folders.Children.Values)
                if (child["path"] is { } p) result.Add(p);
            foreach (var (k, v) in folders.Values) // ancien format : "1" "D:\\Steam"
                if (int.TryParse(k, out _)) result.Add(v);
        }
        return result
            .Select(p => { try { return Path.GetFullPath(p.Replace(@"\\", @"\")); } catch { return p; } })
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string id, string name)
        => ExcludedIds.Contains(id) || ExcludedNames.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

    private sealed class OwnedResponse { public OwnedBody? Response { get; set; } }
    private sealed class OwnedBody { public List<OwnedGame>? Games { get; set; } }
    private sealed class OwnedGame
    {
        [JsonPropertyName("appid")] public int AppId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("playtime_forever")] public int PlaytimeForever { get; set; }
    }
}
