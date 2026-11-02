using System.Net.Http;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>
/// Tags Steam des jeux (gratuit, sans clé d'API), gardés dans tags.json et relus tous les 30 jours.
/// Valent aussi pour les jeux d'autres plateformes que l'on a retrouvés sur Steam par leur titre.
/// </summary>
public sealed class TagService(HttpClient http)
{
    private sealed class Cache
    {
        public DateTime FetchedAt { get; set; }
        /// <summary>Tags de chaque jeu, du plus voté au moins voté.</summary>
        public Dictionary<int, List<int>> Apps { get; set; } = [];
        public Dictionary<int, string> English { get; set; } = [];
        public Dictionary<int, string> French { get; set; } = [];
    }

    private static string CachePath => Path.Combine(AppPaths.Root, "tags.json");
    private Cache _cache = Json.Load<Cache>(CachePath) ?? new Cache();
    private Dictionary<string, string> _labels = [];

    private static int? AppId(Game g) => g.SteamAppId ?? g.Meta?.SteamAppId;

    /// <summary>Nom français d'un tag d'après son nom anglais.</summary>
    public string Label(string english)
    {
        if (_labels.Count == 0)
            _labels = _cache.English.Where(e => _cache.French.ContainsKey(e.Key)).GroupBy(e => e.Value).ToDictionary(g => g.Key, g => _cache.French[g.First().Key]);
        return _labels.GetValueOrDefault(english, english);
    }

    public void Apply(IEnumerable<Game> games)
    {
        foreach (var g in games)
            g.Tags = AppId(g) is int id && _cache.Apps.TryGetValue(id, out var tags)
                ? tags.Select(t => _cache.English.GetValueOrDefault(t)).OfType<string>().ToList()
                : [];
    }

    /// <summary>Télécharge ce qui manque. Vrai si de nouveaux tags sont arrivés.</summary>
    public async Task<bool> FetchMissingAsync(IEnumerable<Game> games, CancellationToken ct)
    {
        try
        {
            var stale = DateTime.Now - _cache.FetchedAt > TimeSpan.FromDays(30);
            var ids = games.Select(AppId).OfType<int>().Distinct().Where(id => stale || !_cache.Apps.ContainsKey(id)).ToList();
            if (ids.Count == 0) return false;

            if (stale || _cache.English.Count == 0)
            {
                _cache.English = await TagNamesAsync("english", ct);
                _cache.French = await TagNamesAsync("french", ct);
                _labels = [];
            }
            foreach (var chunk in ids.Chunk(40))
            {
                var input = JsonSerializer.Serialize(new
                {
                    ids = chunk.Select(id => new { appid = id }),
                    context = new { language = "english", country_code = "FR" },
                    data_request = new { include_tag_count = 20 },
                });
                using var res = await http.GetAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), ct);
                if (!res.IsSuccessStatusCode) continue;
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("store_items", out var items)) continue;
                foreach (var it in items.EnumerateArray())
                {
                    if (!it.TryGetProperty("appid", out var appId)) continue;
                    // Jeu sans tag : noté quand même, pour ne pas le redemander à chaque scan.
                    _cache.Apps[appId.GetInt32()] = it.TryGetProperty("tags", out var tags)
                        ? tags.EnumerateArray().Select(t => t.GetProperty("tagid").GetInt32()).ToList()
                        : [];
                }
            }
            _cache.FetchedAt = DateTime.Now;
            Json.Save(CachePath, _cache);
            Log.Info($"Tags Steam : {ids.Count} jeu(x) mis à jour");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Error("Tags Steam", ex);
            return false;
        }
    }

    private async Task<Dictionary<int, string>> TagNamesAsync(string language, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://api.steampowered.com/IStoreService/GetTagList/v1/?language={language}", ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("response").GetProperty("tags").EnumerateArray()
            .ToDictionary(t => t.GetProperty("tagid").GetInt32(), t => t.GetProperty("name").GetString() ?? "");
    }
}
