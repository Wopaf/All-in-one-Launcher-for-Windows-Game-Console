using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Scanners;
using Microsoft.Data.Sqlite;

namespace MaRogAlly.Services;

/// <summary>Un succès, quelle que soit sa plateforme d'origine.</summary>
public sealed class Achievement
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Icon { get; set; }
    public string? IconLocked { get; set; }
    public bool Unlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }
    /// <summary>Part des joueurs qui l'ont débloqué (0 à 100), si connue.</summary>
    public double? Rarity { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>Succès d'un jeu : détail complet (Steam, GOG) ou seulement les compteurs (Epic, Battle.net… via Galaxy).</summary>
public sealed class AchievementGame
{
    public string Key { get; set; } = "";
    public Platform Platform { get; set; }
    public string Title { get; set; } = "";
    public Game? Game { get; set; }
    public int Total { get; set; }
    public int Unlocked { get; set; }
    public List<Achievement> Items { get; set; } = [];

    public bool HasDetails => Items.Count > 0;
    public double Ratio => Total == 0 ? 0 : (double)Unlocked / Total;
    public bool Completed => Total > 0 && Unlocked >= Total;
    public DateTime? LastUnlock => Items.Where(a => a.Unlocked).Max(a => a.UnlockedAt);
    public string DisplayTitle => Game?.DisplayTitle ?? Title;
}

/// <summary>
/// Rassemble les succès de toutes les plateformes : l'API Web de Steam (clé d'API) pour Steam,
/// et la base locale de GOG Galaxy pour les autres plateformes qui y sont connectées.
/// </summary>
public sealed class AchievementService(HttpClient http, AppSettings settings)
{
    private static string CachePath => Path.Combine(AppPaths.Root, "achievements.json");
    private static string IconsDir => Path.Combine(AppPaths.Root, "achievements");

    private sealed class SteamEntry
    {
        public int AppId { get; set; }
        public DateTime FetchedAt { get; set; }
        public List<Achievement> Items { get; set; } = [];
    }

    /// <summary>Fiche des succès d'un jeu Steam en français (noms, descriptions, icônes) : publique, même si le profil est privé.</summary>
    private sealed class SchemaEntry
    {
        public int AppId { get; set; }
        public DateTime FetchedAt { get; set; }
        public List<Achievement> Items { get; set; } = [];
    }

    private sealed class Cache
    {
        public List<SteamEntry> Steam { get; set; } = [];
        public List<SchemaEntry> Schemas { get; set; } = [];
    }

    private static readonly Cache Saved = Json.Load<Cache>(CachePath) ?? new Cache();
    private readonly Dictionary<int, SteamEntry> _steam = Saved.Steam.ToDictionary(e => e.AppId);
    private readonly Dictionary<int, SchemaEntry> _schemas = Saved.Schemas.ToDictionary(e => e.AppId);

    /// <summary>Steam a refusé l'accès aux succès : les « Détails du jeu » du profil ne sont pas publics.</summary>
    public bool SteamProfilePrivate { get; private set; }

    private sealed class PrivateProfileException : Exception;

    /// <summary>Succès connus sans réseau : cache Steam et base Galaxy.</summary>
    public List<AchievementGame> Load(IReadOnlyList<Game> games) => Merge(games, ReadGalaxy());

    /// <summary>Met à jour les succès Steam des jeux joués (si une clé d'API est renseignée), puis renvoie la liste complète.</summary>
    public async Task<List<AchievementGame>> RefreshAsync(IReadOnlyList<Game> games, Action<int, int>? progress, CancellationToken ct)
    {
        var galaxy = ReadGalaxy();
        var key = settings.SteamApiKey;
        var steamId = string.IsNullOrWhiteSpace(settings.SteamId) ? SteamScanner.DetectSteamId() : settings.SteamId;
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(steamId))
        {
            // Jeux Steam joués au moins une fois (ou dont Galaxy connaît des succès débloqués).
            var counted = galaxy.Where(g => g.Platform == Platform.Steam && g.Unlocked > 0).Select(g => g.Key).ToHashSet();
            var todo = games
                .Where(g => g.SteamAppId is not null && g.Platform == Platform.Steam)
                .Where(g => (g.PlaytimeMinutes ?? 0) > 0 || g.LastPlayedAny is not null || counted.Contains($"steam_{g.SteamAppId}"))
                .Where(g => NeedsRefresh(g))
                .Select(g => g.SteamAppId!.Value)
                .Distinct()
                .ToList();

            var done = 0;
            SteamProfilePrivate = false;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(todo.Select(async appId =>
            {
                try
                {
                    await gate.WaitAsync(stop.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
                try
                {
                    var items = await FetchSteamAsync(appId, key, steamId, stop.Token);
                    lock (_steam) _steam[appId] = new SteamEntry { AppId = appId, FetchedAt = DateTime.Now, Items = items };
                }
                catch (PrivateProfileException)
                {
                    // Inutile d'interroger les autres jeux : la réponse serait la même.
                    SteamProfilePrivate = true;
                    stop.Cancel();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception ex) { Log.Error($"Succès Steam {appId}", ex); }
                finally
                {
                    gate.Release();
                    progress?.Invoke(Interlocked.Increment(ref done), todo.Count);
                }
            }));
            if (SteamProfilePrivate) Log.Info("Succès Steam : profil privé (« Détails du jeu » à rendre public)");

            // Succès Steam connus par Galaxy (souvent en anglais) : fiches françaises, renouvelées chaque mois.
            var schemas = galaxy
                .Where(g => g.Platform == Platform.Steam && g.HasDetails && int.TryParse(g.Key["steam_".Length..], out _))
                .Select(g => int.Parse(g.Key["steam_".Length..]))
                .Where(id => !_steam.TryGetValue(id, out var e) || e.Items.Count == 0)
                .Where(id => !_schemas.TryGetValue(id, out var s) || DateTime.Now - s.FetchedAt > TimeSpan.FromDays(30))
                .ToList();
            var schemaDone = 0;
            await Task.WhenAll(schemas.Select(async appId =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var items = await FetchSchemaAsync(appId, key, ct);
                    lock (_steam) _schemas[appId] = new SchemaEntry { AppId = appId, FetchedAt = DateTime.Now, Items = items.Values.ToList() };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception ex) { Log.Error($"Fiche des succès Steam {appId}", ex); }
                finally
                {
                    gate.Release();
                    progress?.Invoke(Interlocked.Increment(ref schemaDone), schemas.Count);
                }
            }));

            if (todo.Count > 0 || schemas.Count > 0)
            {
                lock (_steam) Json.Save(CachePath, new Cache { Steam = _steam.Values.ToList(), Schemas = _schemas.Values.ToList() });
                Log.Info($"Succès Steam : {todo.Count} jeu(x) et {schemas.Count} fiche(s) mis à jour");
            }
        }
        return Merge(games, galaxy);
    }

    /// <summary>À récupérer si jamais vu, joué depuis, ou vieux d'une semaine.</summary>
    private bool NeedsRefresh(Game g)
    {
        if (!_steam.TryGetValue(g.SteamAppId!.Value, out var e)) return true;
        if (g.LastPlayedAny is { } last && last > e.FetchedAt) return true;
        return DateTime.Now - e.FetchedAt > TimeSpan.FromDays(7);
    }

    // ---------------- Steam (API Web) ----------------

    /// <summary>Succès du joueur, noms et icônes du jeu (en français) et rareté mondiale. Liste vide si le jeu n'a pas de succès.</summary>
    private async Task<List<Achievement>> FetchSteamAsync(int appId, string key, string steamId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?appid={appId}&key={key}&steamid={steamId}&l=french", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new PrivateProfileException();
        if (!res.IsSuccessStatusCode) return []; // pas de statistiques pour ce jeu
        using var player = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!player.RootElement.TryGetProperty("playerstats", out var stats) || !stats.TryGetProperty("achievements", out var list)) return [];

        var items = new Dictionary<string, Achievement>();
        foreach (var a in list.EnumerateArray())
        {
            var id = Str(a, "apiname");
            if (id is null) continue;
            var unlocked = a.TryGetProperty("achieved", out var ach) && ach.GetInt32() == 1;
            items[id] = new Achievement
            {
                Id = id,
                Name = Str(a, "name") ?? id,
                Description = Str(a, "description") ?? "",
                Unlocked = unlocked,
                UnlockedAt = unlocked && a.TryGetProperty("unlocktime", out var t) && t.GetInt64() > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).LocalDateTime : null,
            };
        }
        if (items.Count == 0) return [];

        foreach (var (id, def) in await FetchSchemaAsync(appId, key, ct))
            if (items.TryGetValue(id, out var item)) ApplySchema(item, def);

        using (var global = await GetJsonAsync($"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}", ct))
        {
            if (global is not null && global.RootElement.TryGetProperty("achievementpercentages", out var p) && p.TryGetProperty("achievements", out var pcts))
                foreach (var x in pcts.EnumerateArray())
                {
                    if (Str(x, "name") is not { } id || !items.TryGetValue(id, out var item) || !x.TryGetProperty("percent", out var v)) continue;
                    item.Rarity = v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                        : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                }
        }
        return items.Values.ToList();
    }

    /// <summary>Fiche des succès du jeu en français : noms, descriptions, icônes, succès secrets.</summary>
    private async Task<Dictionary<string, Achievement>> FetchSchemaAsync(int appId, string key, CancellationToken ct)
    {
        var defs = new Dictionary<string, Achievement>();
        using var schema = await GetJsonAsync($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={key}&appid={appId}&l=french", ct);
        if (schema is null || !schema.RootElement.TryGetProperty("game", out var game)
            || !game.TryGetProperty("availableGameStats", out var avail) || !avail.TryGetProperty("achievements", out var list)) return defs;
        foreach (var d in list.EnumerateArray())
        {
            if (Str(d, "name") is not { } id) continue;
            defs[id] = new Achievement
            {
                Id = id,
                Name = Str(d, "displayName") ?? "",
                Description = Str(d, "description") ?? "",
                Icon = Str(d, "icon"),
                IconLocked = Str(d, "icongray"),
                Hidden = d.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() == 1,
            };
        }
        return defs;
    }

    /// <summary>Remplace nom, description et icônes par ceux de la fiche française (s'ils existent).</summary>
    private static void ApplySchema(Achievement item, Achievement def)
    {
        if (def.Name.Length > 0) item.Name = def.Name;
        if (def.Description.Length > 0) item.Description = def.Description;
        item.Icon = def.Icon ?? item.Icon;
        item.IconLocked = def.IconLocked ?? item.IconLocked;
        item.Hidden |= def.Hidden;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------- GOG Galaxy (toutes plateformes connectées) ----------------

    private static List<AchievementGame> ReadGalaxy()
    {
        var result = new Dictionary<string, AchievementGame>();
        if (!File.Exists(GalaxyLibrary.DbPath)) return [];

        // Copie pour ne pas gêner Galaxy, qui garde la base ouverte.
        var tmp = Path.Combine(Path.GetTempPath(), "marogally-galaxy-achievements.db");
        try
        {
            File.Copy(GalaxyLibrary.DbPath, tmp, true);
            if (File.Exists(GalaxyLibrary.DbPath + "-wal")) File.Copy(GalaxyLibrary.DbPath + "-wal", tmp + "-wal", true);
            else if (File.Exists(tmp + "-wal")) File.Delete(tmp + "-wal");

            using var cn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False");
            cn.Open();

            // Titre et compteurs (« myAchievementsCount ») des jeux joués ou possédés.
            Read(cn, """
                SELECT gp.releaseKey, t.type, gp.value
                FROM GamePieces gp
                JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId
                WHERE t.type = 'myAchievementsCount'
                   OR (t.type = 'title' AND (gp.releaseKey IN (SELECT releaseKey FROM LibraryReleases)
                                             OR gp.releaseKey IN (SELECT gameReleaseKey FROM Achievements)))
                """, r =>
            {
                var key = r.GetString(0);
                if (PlatformOf(key) is not { } platform) return;
                var game = result.TryGetValue(key, out var g) ? g : result[key] = new AchievementGame { Key = key, Platform = platform };
                using var doc = JsonDocument.Parse(r.GetString(2));
                var root = doc.RootElement;
                if (r.GetString(1) == "title")
                    game.Title = Str(root, "title") ?? game.Title;
                else
                {
                    // Une ligne par compte connecté, parfois vide : on garde la plus complète.
                    game.Total = Math.Max(game.Total, Int(root, "all"));
                    game.Unlocked = Math.Max(game.Unlocked, Int(root, "unlocked"));
                }
            });

            // Détail des succès, en français si possible.
            Read(cn, """
                SELECT a.gameReleaseKey, a.apikey, a.imageUnlockedUrl, a.imageLockedUrl, a.isVisible, a.rarity,
                       (SELECT l.name FROM LocalizedAchievements l LEFT JOIN Languages lg ON lg.id = l.languageId
                        WHERE l.gameReleaseKey = a.gameReleaseKey AND l.apikey = a.apikey
                        ORDER BY CASE lg.code WHEN 'fr-FR' THEN 0 WHEN 'en-US' THEN 1 ELSE 2 END LIMIT 1),
                       (SELECT l.description FROM LocalizedAchievements l LEFT JOIN Languages lg ON lg.id = l.languageId
                        WHERE l.gameReleaseKey = a.gameReleaseKey AND l.apikey = a.apikey
                        ORDER BY CASE lg.code WHEN 'fr-FR' THEN 0 WHEN 'en-US' THEN 1 ELSE 2 END LIMIT 1),
                       (SELECT MAX(u.isUnlocked) FROM UserAchievements u WHERE u.gameReleaseKey = a.gameReleaseKey AND u.apikey = a.apikey),
                       (SELECT MAX(u.unlockTime) FROM UserAchievements u WHERE u.gameReleaseKey = a.gameReleaseKey AND u.apikey = a.apikey AND u.isUnlocked = 1)
                FROM Achievements a
                """, r =>
            {
                if (!result.TryGetValue(r.GetString(0), out var game)) return;
                var unlocked = !r.IsDBNull(8) && r.GetInt32(8) == 1;
                game.Items.Add(new Achievement
                {
                    Id = r.GetString(1),
                    Icon = r.IsDBNull(2) ? null : r.GetString(2),
                    IconLocked = r.IsDBNull(3) ? null : r.GetString(3),
                    Hidden = !r.IsDBNull(4) && r.GetInt32(4) == 0,
                    Rarity = r.IsDBNull(5) ? null : r.GetDouble(5),
                    Name = r.IsDBNull(6) ? r.GetString(1) : r.GetString(6),
                    Description = r.IsDBNull(7) ? "" : r.GetString(7),
                    Unlocked = unlocked,
                    UnlockedAt = unlocked && !r.IsDBNull(9) && DateTime.TryParse(r.GetString(9), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at.ToLocalTime() : null,
                });
            });
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des succès GOG Galaxy impossible", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(tmp); File.Delete(tmp + "-wal"); } catch { }
        }

        foreach (var g in result.Values.Where(g => g.HasDetails))
        {
            g.Total = g.Items.Count;
            g.Unlocked = g.Items.Count(a => a.Unlocked);
        }
        return result.Values.ToList();
    }

    private static void Read(SqliteConnection cn, string sql, Action<SqliteDataReader> row)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static Platform? PlatformOf(string releaseKey)
    {
        var sep = releaseKey.IndexOf('_');
        if (sep <= 0) return null;
        var prefix = releaseKey[..sep];
        foreach (var p in Enum.GetValues<Platform>())
            if (GalaxyLibrary.PrefixesFor(p).Contains(prefix, StringComparer.OrdinalIgnoreCase)) return p;
        return null;
    }

    // ---------------- Fusion avec la bibliothèque ----------------

    private List<AchievementGame> Merge(IReadOnlyList<Game> games, List<AchievementGame> galaxy)
    {
        var byKey = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<(Platform, string), Game>();
        foreach (var g in games.Where(g => !g.IsHidden))
        {
            foreach (var prefix in GalaxyLibrary.PrefixesFor(g.Platform)) byKey.TryAdd($"{prefix}_{g.PlatformId}", g);
            if (g.SteamAppId is { } sid) byKey.TryAdd($"steam_{sid}", g);
            byTitle.TryAdd((g.Platform, Text.Normalize(g.Title)), g);
        }

        var all = galaxy.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
        // Succès Steam venus de Galaxy : textes français de la fiche Steam.
        lock (_steam)
            foreach (var (appId, schema) in _schemas)
                if (all.TryGetValue($"steam_{appId}", out var g) && g.HasDetails)
                {
                    var defs = schema.Items.ToDictionary(a => a.Id);
                    foreach (var item in g.Items)
                        if (defs.TryGetValue(item.Id, out var def)) ApplySchema(item, def);
                }
        // Les données Steam de l'API, plus complètes et à jour, remplacent celles de Galaxy.
        lock (_steam)
            foreach (var e in _steam.Values.Where(e => e.Items.Count > 0))
            {
                var key = $"steam_{e.AppId}";
                var title = all.TryGetValue(key, out var old) ? old.Title : "";
                all[key] = new AchievementGame
                {
                    Key = key, Platform = Platform.Steam, Title = title, Items = e.Items,
                    Total = e.Items.Count, Unlocked = e.Items.Count(a => a.Unlocked),
                };
            }

        foreach (var a in all.Values)
            a.Game = byKey.GetValueOrDefault(a.Key) ?? byTitle.GetValueOrDefault((a.Platform, Text.Normalize(a.Title)));

        return all.Values
            .Where(a => a.Total > 0 && (a.Game is not null || a.Title.Length > 0))
            .OrderByDescending(a => a.LastUnlock ?? DateTime.MinValue)
            .ThenByDescending(a => a.Unlocked)
            .ThenBy(a => a.DisplayTitle)
            .ToList();
    }

    // ---------------- Icônes ----------------

    private static readonly SemaphoreSlim IconGate = new(6);

    /// <summary>Icône d'un succès téléchargée une seule fois (chemin local), ou null.</summary>
    public async Task<string?> IconAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20];
        var path = Path.Combine(IconsDir, name + ".img");
        if (File.Exists(path)) return path;
        await IconGate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            foreach (var candidate in IconAddresses(url))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(candidate, ct);
                    if (bytes.Length < 64) continue;
                    Directory.CreateDirectory(IconsDir);
                    var tmp = path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, path, true);
                    return path;
                }
                catch (HttpRequestException) { } // absente de ce serveur : on essaie le suivant
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { IconGate.Release(); }
    }

    /// <summary>L'adresse donnée par Steam, puis ses nouveaux serveurs : une partie des icônes a quitté l'ancien (erreur 404).</summary>
    private static IEnumerable<string> IconAddresses(string url)
    {
        yield return url;
        const string old = "/steamcommunity/public/images/apps/";
        var i = url.IndexOf(old, StringComparison.Ordinal);
        if (i < 0) yield break;
        var tail = url[(i + old.Length)..];
        yield return "https://shared.fastly.steamstatic.com/community_assets/images/apps/" + tail;
        yield return "https://shared.cloudflare.steamstatic.com/community_assets/images/apps/" + tail;
    }
}
