using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaRogAlly.Core;
using MaRogAlly.Metadata;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>Jeu mis en avant dans l'onglet Découverte (données du magasin Steam).</summary>
public sealed class DiscoverGame : ObservableObject
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime? Release { get; set; }
    public bool ComingSoon { get; set; }
    public int? ReviewPercent { get; set; }
    public string? ReviewLabel { get; set; }
    public bool IsFree { get; set; }
    public string? Price { get; set; }
    public string? OriginalPrice { get; set; }
    public int DiscountPercent { get; set; }
    /// <summary>Classement et pic de joueurs simultanés (section « Les plus joués »).</summary>
    public int? Rank { get; set; }
    public int? PeakPlayers { get; set; }
    public List<string> Genres { get; set; } = [];
    /// <summary>Visuels fournis par le magasin (jaquette, fond, capsule large en dernier recours).</summary>
    public List<string> CoverUrls { get; set; } = [];
    public List<string> HeroUrls { get; set; } = [];

    private string? _coverPath, _heroPath;
    [JsonIgnore] public string? CoverPath { get => _coverPath; set => Set(ref _coverPath, value); }
    [JsonIgnore] public string? HeroPath { get => _heroPath; set => Set(ref _heroPath, value); }

    /// <summary>Jeu correspondant dans la bibliothèque, s'il est déjà possédé.</summary>
    private Game? _owned;
    [JsonIgnore]
    public Game? Owned
    {
        get => _owned;
        set { if (Set(ref _owned, value)) { OnPropertyChanged(nameof(IsOwned)); OnPropertyChanged(nameof(OwnedText)); OnPropertyChanged(nameof(CardBadge)); } }
    }
    [JsonIgnore] public bool IsOwned => Owned is not null;
    [JsonIgnore] public string OwnedText => Owned?.IsInstalled == true ? "Installé" : "Possédé";

    [JsonIgnore] public string? RankText => Rank is int r ? $"N° {r}" : null;
    [JsonIgnore]
    public string? PlayersText => PeakPlayers is int p
        ? $"{(p >= 1_000_000 ? (p / 1_000_000.0).ToString("0.#", Fr) + " M" : p >= 1000 ? (p / 1000.0).ToString("0", Fr) + " k" : p.ToString())} joueurs au pic du jour"
        : null;
    [JsonIgnore] public string? PriceText => IsFree ? "Gratuit" : Price;
    [JsonIgnore] public string? DiscountText => DiscountPercent > 0 ? $"-{DiscountPercent} %" : null;
    /// <summary>Badge de la jaquette : « Possédé », sinon le prix (avec la remise éventuelle).</summary>
    [JsonIgnore]
    public string? CardBadge => IsOwned ? "Possédé"
        : PriceText is { } price ? (DiscountText is { } d ? $"{d}  {price}" : price)
        : null;
    [JsonIgnore] public bool IsDiscounted => DiscountPercent > 0 || IsFree;
    /// <summary>Recherche Instant Gaming limitée aux versions PC Steam (jamais de console).</summary>
    [JsonIgnore]
    public string InstantGamingUrl => "https://www.instant-gaming.com/fr/rechercher/?q=" + Uri.EscapeDataString(Name)
        + "&platform%5B0%5D=1&type%5B0%5D=Steam";
    [JsonIgnore] public string? ReviewText => ReviewPercent is int r ? $"{r} % d'avis positifs" : null;
    [JsonIgnore]
    public string? ReleaseText => Release is { } d
        ? (ComingSoon || d > DateTime.Now ? $"Sortie le {d.ToString("d MMMM yyyy", Fr)}" : $"Sorti le {d.ToString("d MMMM yyyy", Fr)}")
        : ComingSoon ? "Prochainement" : null;
    [JsonIgnore] public string? GenresText => Genres.Count > 0 ? string.Join(" · ", Genres.Take(3)) : null;
    /// <summary>Petite ligne sous la jaquette.</summary>
    [JsonIgnore] public string? CardLine => PlayersText is not null ? PlayersText.Replace(" au pic du jour", "") : ComingSoon ? ReleaseText : PriceText;

    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}";

    private bool _isWished;
    /// <summary>Dans la liste de souhaits (cœur sur la jaquette).</summary>
    [JsonIgnore] public bool IsWished { get => _isWished; set => Set(ref _isWished, value); }
    [JsonIgnore] public string WishKey => $"steam-{AppId}";
}

public sealed class DiscoverSection
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<DiscoverGame> Games { get; set; } = [];
}

/// <summary>
/// Tendances du magasin Steam, sans clé d'API : jeux les plus joués (ISteamChartsService),
/// nouveautés, meilleures ventes et sorties à venir (featuredcategories), détaillés par IStoreBrowseService.
/// </summary>
public sealed class DiscoverService(HttpClient http)
{
    private static readonly string CachePath = Path.Combine(AppPaths.Root, "discover.json");
    private readonly ImageCache _images = new(http);

    /// <summary>Dernières sections connues (affichage immédiat au démarrage).</summary>
    public static List<DiscoverSection> LoadCached() => Json.Load<List<DiscoverSection>>(CachePath) ?? [];

    public async Task<List<DiscoverSection>> FetchAsync(CancellationToken ct)
    {
        var sections = new List<DiscoverSection>();
        try
        {
            // Recherche Steam limitée aux jeux (category1=998) : pas de DLC, démos ni bandes-son.
            var played = await MostPlayedAsync(ct);
            var popularNew = await SearchAsync("popularnew", 60, ct);
            var sellers = await SearchAsync("topsellers", 20, ct);
            var soon = await SearchAsync("popularcomingsoon", 20, ct);
            var ids = played.Select(p => p.AppId).Concat(popularNew).Concat(sellers).Concat(soon).Distinct().ToList();
            var details = await DetailsAsync(ids, ct);

            // Copie : un même jeu peut figurer dans plusieurs catégories.
            DiscoverGame? Make(int id) => details.TryGetValue(id, out var d)
                ? JsonSerializer.Deserialize<DiscoverGame>(JsonSerializer.Serialize(d))
                : null;

            var mostPlayed = played.Select(p =>
            {
                var g = Make(p.AppId);
                if (g is null) return null;
                g.Rank = p.Rank;
                g.PeakPlayers = p.Peak;
                return g;
            }).OfType<DiscoverGame>().ToList();

            // « Nouveautés en tendance » : les jeux populaires sortis ces quatre derniers mois.
            var trending = popularNew.Select(Make).OfType<DiscoverGame>()
                .Where(g => !g.ComingSoon && g.Release is { } r && r <= DateTime.Now && DateTime.Now - r < TimeSpan.FromDays(120))
                .Take(20).ToList();

            sections.Add(new DiscoverSection { Key = "played", Title = "Les plus joués", Subtitle = "En ce moment sur Steam", Games = mostPlayed });
            sections.Add(new DiscoverSection { Key = "new", Title = "Nouveautés en tendance", Subtitle = "Sorties récentes populaires", Games = trending });
            sections.Add(new DiscoverSection
            {
                Key = "sellers", Title = "Meilleures ventes", Subtitle = "Les plus achetés sur Steam",
                Games = sellers.Select(Make).OfType<DiscoverGame>().ToList(),
            });
            sections.Add(new DiscoverSection
            {
                Key = "soon", Title = "Prochainement", Subtitle = "Les sorties les plus attendues",
                Games = soon.Select(Make).OfType<DiscoverGame>().Select(g => { g.ComingSoon = true; return g; }).ToList(),
            });
            sections.RemoveAll(s => s.Games.Count == 0);
            if (sections.Count > 0) Json.Save(CachePath, sections);
            Log.Info($"Découverte : {string.Join(", ", sections.Select(s => $"{s.Title} ({s.Games.Count})"))}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("Découverte indisponible", ex);
        }
        return sections;
    }

    /// <summary>Fiches à jour de ces jeux (prix, promotions) ; vide si le magasin ne répond pas.</summary>
    public async Task<Dictionary<int, DiscoverGame>> DetailsOfAsync(List<int> ids, CancellationToken ct)
    {
        try { return await DetailsAsync(ids, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Fiches de la liste de souhaits", ex);
            return [];
        }
    }

    /// <summary>Recherche dans le magasin Steam (jeux uniquement), détaillée comme les tendances.</summary>
    public async Task<List<DiscoverGame>> FindAsync(string term, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=french&cc=FR", ct);
        var ids = new List<int>();
        if (doc?.RootElement.TryGetProperty("items", out var items) == true)
            foreach (var i in items.EnumerateArray())
                if (i.TryGetProperty("type", out var t) && t.GetString() == "app" && i.TryGetProperty("id", out var id) && id.TryGetInt32(out var appId))
                    ids.Add(appId);
        var details = await DetailsAsync(ids.Distinct().Take(12).ToList(), ct);
        return ids.Distinct().Select(id => details.GetValueOrDefault(id)).OfType<DiscoverGame>().ToList();
    }

    /// <summary>Jaquettes et visuels de fond, partagés avec le cache des fiches (clé steam-{appid}).</summary>
    public async Task LoadImagesAsync(IEnumerable<DiscoverGame> games, CancellationToken ct)
    {
        var all = games.GroupBy(g => g.AppId).ToList();
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (group, token) =>
        {
            var key = $"steam-{group.Key}";
            var first = group.First();
            // Jaquette verticale d'abord ; à défaut (jeu récent), la capsule large recadrée.
            var cover = ImageCache.Existing(key, "cover")
                        ?? await _images.GetAsync(key, "cover", first.CoverUrls.Take(2).Concat(SteamStore.CoverUrls(group.Key)).Concat(first.CoverUrls.Skip(2)), token);
            var hero = ImageCache.Existing(key, "hero")
                       ?? await _images.GetAsync(key, "hero", first.HeroUrls.Concat(SteamStore.HeroUrls(group.Key)), token);
            foreach (var g in group)
            {
                g.CoverPath = cover;
                g.HeroPath = hero;
            }
        });
    }

    /// <summary>Applique tout de suite les visuels déjà sur disque.</summary>
    public static void ApplyCachedImages(IEnumerable<DiscoverGame> games)
    {
        foreach (var g in games)
        {
            g.CoverPath = ImageCache.Existing($"steam-{g.AppId}", "cover");
            g.HeroPath = ImageCache.Existing($"steam-{g.AppId}", "hero");
        }
    }

    // ---------------- Sources ----------------

    private async Task<List<(int AppId, int Rank, int Peak)>> MostPlayedAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://api.steampowered.com/ISteamChartsService/GetMostPlayedGames/v1/", ct);
        var list = new List<(int, int, int)>();
        if (doc?.RootElement.TryGetProperty("response", out var r) == true && r.TryGetProperty("ranks", out var ranks))
            foreach (var e in ranks.EnumerateArray().Take(20))
                list.Add((e.GetProperty("appid").GetInt32(), e.GetProperty("rank").GetInt32(),
                    e.TryGetProperty("peak_in_game", out var p) ? p.GetInt32() : 0));
        return list;
    }

    /// <summary>Résultats d'une liste du magasin (popularnew, topsellers, popularcomingsoon), jeux uniquement.</summary>
    private async Task<List<int>> SearchAsync(string filter, int count, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"https://store.steampowered.com/search/results/?filter={filter}&json=1&cc=FR&l=french&count={count}&category1=998", ct);
        var ids = new List<int>();
        if (doc?.RootElement.TryGetProperty("items", out var items) != true) return ids;
        foreach (var i in items.EnumerateArray())
        {
            // L'identifiant n'est donné que dans l'adresse de l'image : …/apps/{appid}/…
            var logo = i.TryGetProperty("logo", out var l) ? l.GetString() : null;
            var m = logo is null ? null : System.Text.RegularExpressions.Regex.Match(logo, @"/apps/(\d+)/");
            if (m is { Success: true } && int.TryParse(m.Groups[1].Value, out var id) && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    private async Task<Dictionary<int, DiscoverGame>> DetailsAsync(List<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, DiscoverGame>();
        foreach (var chunk in ids.Chunk(40))
        {
            var input = JsonSerializer.Serialize(new
            {
                ids = chunk.Select(id => new { appid = id }),
                context = new { language = "french", country_code = "FR" },
                data_request = new
                {
                    include_basic_info = true, include_release = true, include_reviews = true,
                    include_all_purchase_options = true, include_assets = true,
                },
            });
            using var doc = await GetJsonAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), ct);
            if (doc?.RootElement.TryGetProperty("response", out var r) != true || !r.TryGetProperty("store_items", out var items)) continue;
            foreach (var e in items.EnumerateArray())
            {
                if (!e.TryGetProperty("success", out var ok) || ok.GetInt32() != 1 || !e.TryGetProperty("name", out var name)) continue;
                var g = new DiscoverGame
                {
                    AppId = e.GetProperty("appid").GetInt32(),
                    Name = name.GetString() ?? "",
                    IsFree = e.TryGetProperty("is_free", out var free) && free.GetBoolean(),
                };
                if (e.TryGetProperty("basic_info", out var bi) && bi.TryGetProperty("short_description", out var sd))
                    g.Description = Text.StripHtml(sd.GetString() ?? "");
                if (e.TryGetProperty("release", out var rel))
                {
                    if (rel.TryGetProperty("steam_release_date", out var rd))
                        g.Release = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64()).LocalDateTime;
                    else if (rel.TryGetProperty("original_release_date", out var od))
                        g.Release = DateTimeOffset.FromUnixTimeSeconds(od.GetInt64()).LocalDateTime;
                    if (rel.TryGetProperty("is_coming_soon", out var cs) && cs.GetBoolean()) g.ComingSoon = true;
                }
                if (e.TryGetProperty("reviews", out var rv) && rv.TryGetProperty("summary_filtered", out var sf)
                    && sf.TryGetProperty("percent_positive", out var pp) && sf.TryGetProperty("review_count", out var rc) && rc.GetInt32() > 0)
                    g.ReviewPercent = pp.GetInt32();
                if (e.TryGetProperty("best_purchase_option", out var bp))
                {
                    g.Price = bp.TryGetProperty("formatted_final_price", out var fp) ? fp.GetString() : null;
                    if (bp.TryGetProperty("discount_pct", out var dp)) g.DiscountPercent = dp.GetInt32();
                    g.OriginalPrice = bp.TryGetProperty("formatted_original_price", out var op) ? op.GetString() : null;
                }
                if (e.TryGetProperty("assets", out var assets) && assets.TryGetProperty("asset_url_format", out var fmt))
                {
                    string? Url(string name) => assets.TryGetProperty(name, out var f) && f.GetString() is { Length: > 0 } file
                        ? "https://shared.akamai.steamstatic.com/store_item_assets/" + fmt.GetString()!.Replace("${FILENAME}", file)
                        : null;
                    g.CoverUrls = new[] { Url("library_capsule_2x"), Url("library_capsule"), Url("main_capsule"), Url("header") }.OfType<string>().ToList();
                    g.HeroUrls = new[] { Url("library_hero"), Url("hero_capsule"), Url("main_capsule") }.OfType<string>().ToList();
                }
                result[g.AppId] = g;
            }
        }
        return result;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // délai dépassé
        catch (HttpRequestException ex)
        {
            Log.Error($"Découverte : {url}", ex);
            return null;
        }
    }
}
