using System.Text.Json;
using System.Text.RegularExpressions;
using MaRogAlly.Core;

namespace MaRogAlly.Metadata;

/// <summary>Jeu des tendances IGDB : toutes plateformes, avec son identifiant Steam s'il y est vendu.</summary>
public sealed class IgdbTrendGame
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public DateTime? Release { get; init; }
    /// <summary>Plateformes connues, en libellés courts (PC, PS5, Xbox Series, Switch 2…), dans un ordre fixe.</summary>
    public List<string> Platforms { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public string? CoverUrl { get; init; }
    public List<string> HeroUrls { get; init; } = [];
    public List<string> Screenshots { get; init; } = [];
    public int? SteamAppId { get; init; }
}

/// <summary>Tendances IGDB : en tendance (sortis récemment) et les plus attendus (pas encore sortis), du plus fort au moins fort.</summary>
public sealed record IgdbTrends(List<IgdbTrendGame> Trending, List<IgdbTrendGame> Anticipated);

public sealed partial class IgdbClient
{
    /// <summary>
    /// Tendances toutes plateformes, d'après les indicateurs de popularité d'IGDB : visites des fiches, « veut jouer »,
    /// « joue en ce moment », heures regardées sur Twitch, pic de joueurs et souhaits Steam, plus l'attente (hypes) des jeux à venir.
    /// Un jeu n'arrive en tête que s'il est fort sur plusieurs indicateurs. Null si IGDB n'est pas configuré ou ne répond pas.
    /// </summary>
    public async Task<IgdbTrends?> TrendsAsync(CancellationToken ct)
    {
        if (!IsConfigured) return null;
        using var typesDoc = await PostAsync("popularity_types", "fields *; limit 100;", ct);
        if (typesDoc is null || typesDoc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var types = new List<(long Id, string Name)>();
        foreach (var t in typesDoc.RootElement.EnumerateArray())
            if (Str(t, "name") is { } n && t.TryGetProperty("id", out var id) && id.TryGetInt64(out var tid)) types.Add((tid, n.Trim()));
        Log.Info($"IGDB : indicateurs de popularité {string.Join(", ", types.Select(t => $"{t.Id} {t.Name}"))}");

        // Nom exact d'abord, sinon le premier qui le contient.
        long? Type(string name) => types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } exact ? exact.Id
            : types.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } near ? near.Id : null;

        var visits = await RanksAsync(Type("Visits"), 200, ct);
        var want = await RanksAsync(Type("Want to Play"), 200, ct);
        var playing = await RanksAsync(Type("Playing"), 200, ct);
        var watched = await RanksAsync(Type("Hours Watched"), 100, ct);
        var peak = await RanksAsync(Type("Peak Players"), 100, ct);
        var wishlisted = await RanksAsync(Type("Wishlisted"), 100, ct);
        var hyped = await HypedAsync(ct);
        Log.Info($"IGDB : visites {visits.Count}, veut jouer {want.Count}, joue {playing.Count}, Twitch {watched.Count}, pic Steam {peak.Count}, souhaits Steam {wishlisted.Count}, attendus {hyped.Count}");

        var trendScore = new Dictionary<long, double>();
        Add(trendScore, visits, 1.0);
        Add(trendScore, playing, 1.0);
        Add(trendScore, watched, 0.8);
        Add(trendScore, peak, 0.6);
        Add(trendScore, want, 0.5);
        var waitScore = new Dictionary<long, double>();
        Add(waitScore, want, 1.0);
        Add(waitScore, hyped, 1.0);
        Add(waitScore, wishlisted, 0.8);
        Add(waitScore, visits, 0.6);
        if (trendScore.Count == 0 && waitScore.Count == 0) return null;

        var ids = trendScore.OrderByDescending(p => p.Value).Take(200).Select(p => p.Key)
            .Concat(waitScore.OrderByDescending(p => p.Value).Take(200).Select(p => p.Key)).Distinct().ToList();
        var games = new Dictionary<long, (IgdbTrendGame Game, bool Main)>();
        foreach (var chunk in ids.Chunk(200))
            foreach (var g in await GamesAsync(chunk, ct)) games[g.Game.Id] = g;

        var now = DateTime.Now;
        List<IgdbTrendGame> Released(int months) => trendScore.OrderByDescending(p => p.Value)
            .Select(p => games.TryGetValue(p.Key, out var g) ? g : default)
            .Where(g => g.Main && g.Game.Release is { } r && r <= now && r >= now.AddMonths(-months))
            .Select(g => g.Game).Take(20).ToList();
        // Sorties de l'année ; à défaut d'assez de jeux, des deux dernières années.
        var trending = Released(12);
        if (trending.Count < 10) trending = Released(24);
        var anticipated = waitScore.OrderByDescending(p => p.Value)
            .Select(p => games.TryGetValue(p.Key, out var g) ? g : default)
            .Where(g => g.Main && (g.Game.Release is not { } r || r > now))
            .Select(g => g.Game).Take(20).ToList();
        Log.Info($"IGDB : {trending.Count} en tendance ({string.Join(", ", trending.Take(5).Select(g => g.Name))}), " +
                 $"{anticipated.Count} attendus ({string.Join(", ", anticipated.Take(5).Select(g => g.Name))})");
        return new IgdbTrends(trending, anticipated);
    }

    /// <summary>Un classement compte d'autant plus que le jeu y est haut placé (1 pour le premier, presque 0 pour le dernier).</summary>
    private static void Add(Dictionary<long, double> scores, List<long> ranks, double weight)
    {
        for (var i = 0; i < ranks.Count; i++)
            scores[ranks[i]] = scores.GetValueOrDefault(ranks[i]) + weight * (1 - i / (double)ranks.Count);
    }

    private async Task<List<long>> RanksAsync(long? type, int limit, CancellationToken ct)
    {
        var ids = new List<long>();
        if (type is null) return ids;
        using var doc = await PostAsync("popularity_primitives", $"fields game_id,value; where popularity_type = {type}; sort value desc; limit {limit};", ct);
        if (doc?.RootElement.ValueKind != JsonValueKind.Array) return ids;
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("game_id", out var g) && g.TryGetInt64(out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>Jeux pas encore sortis (ou sans date) les plus suivis avant leur sortie.</summary>
    private async Task<List<long>> HypedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var doc = await PostAsync("games", $"fields id; where hypes > 0 & (first_release_date > {now} | first_release_date = null); sort hypes desc; limit 150;", ct);
        var ids = new List<long>();
        if (doc?.RootElement.ValueKind != JsonValueKind.Array) return ids;
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("id", out var g) && g.TryGetInt64(out var id)) ids.Add(id);
        return ids;
    }

    private const string TrendFields = "name,summary,first_release_date,version_parent,cover.image_id,artworks.image_id,screenshots.image_id," +
                                       "platforms.name,genres.name,involved_companies.developer,involved_companies.publisher,involved_companies.company.name,websites.url";

    private async Task<List<(IgdbTrendGame Game, bool Main)>> GamesAsync(long[] ids, CancellationToken ct)
    {
        var where = $"where id = ({string.Join(",", ids)}); limit {ids.Length};";
        // Type de jeu et liens externes : champs qui ont changé de nom chez IGDB, d'où une seconde demande sans eux.
        using var doc = await PostAsync("games", $"fields {TrendFields},game_type,category,external_games.uid,external_games.category; {where}", ct)
                        ?? await PostAsync("games", $"fields {TrendFields},category; {where}", ct);
        var list = new List<(IgdbTrendGame, bool)>();
        if (doc?.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var id) || Str(e, "name") is not { } name) continue;
            // Jeu principal, remake, remaster, version enrichie, portage, extension autonome : pas de DLC, pack ni édition.
            var kind = Int(e, "game_type") ?? Int(e, "category");
            var main = kind is null or 0 or 4 or 8 or 9 or 10 or 11 && !e.TryGetProperty("version_parent", out _);
            string? developer = null, publisher = null;
            if (e.TryGetProperty("involved_companies", out var companies))
                foreach (var c in companies.EnumerateArray())
                {
                    var company = c.TryGetProperty("company", out var co) ? Str(co, "name") : null;
                    if (company is null) continue;
                    if (Bool(c, "developer")) developer ??= company;
                    if (Bool(c, "publisher")) publisher ??= company;
                }
            var shots = ImageIds(e, "screenshots").Select(i => ImageUrl(i, "t_1080p")).ToList();
            list.Add((new IgdbTrendGame
            {
                Id = id,
                Name = name,
                Summary = Str(e, "summary"),
                Release = e.TryGetProperty("first_release_date", out var frd) && frd.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null,
                Platforms = PlatformLabels(Names(e, "platforms")),
                Genres = Names(e, "genres").Take(3).ToList(),
                Developer = developer,
                Publisher = publisher,
                CoverUrl = e.TryGetProperty("cover", out var cover) && Str(cover, "image_id") is { } coverId ? ImageUrl(coverId, "t_cover_big_2x") : null,
                HeroUrls = ImageIds(e, "artworks").Select(i => ImageUrl(i, "t_1080p")).Concat(shots.Take(2)).ToList(),
                Screenshots = shots.Take(8).ToList(),
                SteamAppId = SteamIdOf(e),
            }, main));
        }
        return list;
    }

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>Identifiant Steam : lien externe Steam (catégorie 1), sinon l'adresse de sa page dans les sites du jeu.</summary>
    private static int? SteamIdOf(JsonElement e)
    {
        if (e.TryGetProperty("external_games", out var ext))
            foreach (var x in ext.EnumerateArray())
                if (Int(x, "category") == 1 && int.TryParse(Str(x, "uid"), out var uid)) return uid;
        if (e.TryGetProperty("websites", out var sites))
            foreach (var s in sites.EnumerateArray())
                if (Str(s, "url") is { } url && SteamUrl().Match(url) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var id)) return id;
        return null;
    }

    [GeneratedRegex(@"store\.steampowered\.com/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamUrl();

    /// <summary>Plateformes de jeu courantes en libellés courts, dans un ordre fixe ; les autres (mobile, anciennes consoles) sont ignorées.</summary>
    private static List<string> PlatformLabels(IEnumerable<string> names)
    {
        var all = names.ToList();
        bool Has(string text) => all.Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
        var labels = new List<string>();
        if (Has("Windows") || all.Any(n => n.Equals("PC", StringComparison.OrdinalIgnoreCase))) labels.Add("PC");
        if (Has("PlayStation 5")) labels.Add("PS5");
        if (Has("Xbox Series")) labels.Add("Xbox Series");
        if (Has("Switch 2")) labels.Add("Switch 2");
        if (all.Any(n => n.Equals("Nintendo Switch", StringComparison.OrdinalIgnoreCase))) labels.Add("Switch");
        if (Has("PlayStation 4")) labels.Add("PS4");
        if (all.Any(n => n.Equals("Xbox One", StringComparison.OrdinalIgnoreCase))) labels.Add("Xbox One");
        return labels;
    }
}
