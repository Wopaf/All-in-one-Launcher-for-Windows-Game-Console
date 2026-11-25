using System.Collections.Concurrent;
using System.Net.Http;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Metadata;

/// <summary>Avancement de l'enrichissement : phase (« Visuels », « Fiches de jeux »), compteur, jeu en cours.</summary>
public sealed record EnrichProgress(string Phase, int Done, int Total, string? Current);

/// <summary>
/// Enrichit les jeux : fiche (magasin Steam en français, puis IGDB) et visuels
/// (plateforme, SteamGridDB, Steam, IGDB). Tout est mis en cache sur disque.
/// </summary>
public sealed class MetadataService
{
    private readonly AppSettings _settings;
    private readonly SteamStore _steam;
    private readonly IgdbClient _igdb;
    /// <summary>Client IGDB partagé (tendances de l'onglet Découverte) : une seule limite de 4 requêtes par seconde.</summary>
    public IgdbClient Igdb => _igdb;
    private readonly SteamGridDbClient _grid;
    private readonly ImageCache _images;
    private readonly ConcurrentDictionary<string, GameMetadata> _cache = new();

    private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(7);

    public MetadataService(HttpClient http, AppSettings settings)
    {
        _settings = settings;
        _steam = new SteamStore(http);
        _igdb = new IgdbClient(http, settings);
        _grid = new SteamGridDbClient(http, settings);
        _images = new ImageCache(http);
    }

    private static string FileFor(string key) => Path.Combine(AppPaths.MetadataDir, key + ".json");

    private GameMetadata? Cached(string key)
    {
        if (_cache.TryGetValue(key, out var m)) return m;
        m = Json.Load<GameMetadata>(FileFor(key));
        if (m is not null) _cache[key] = m;
        return m;
    }

    /// <summary>Applique instantanément ce qui est déjà sur disque (au démarrage, avant tout réseau).</summary>
    public void ApplyCached(Game game)
    {
        var key = game.MetadataKey;
        var cached = Cached(key);
        if (game.Platform == Platform.Ankama) cached = AnkamaCatalog.Complete(game.Title, cached);
        if (cached is { NotFound: false } meta) game.Meta = meta;
        game.CoverPath = ImageCache.Existing(key, "cover");
        game.HeroPath = ImageCache.Existing(key, "hero");
        game.LogoPath = ImageCache.Existing(key, "logo");
    }

    /// <summary>
    /// Complète en tâche de fond tous les jeux auxquels il manque quelque chose, en deux passes :
    /// 1. visuels déjà connus (fournis par la plateforme, CDN Steam) : rapide, en parallèle, sans limite ;
    /// 2. fiches complètes (magasin Steam, IGDB, SteamGridDB) : lent, le magasin Steam limitant le débit.
    /// </summary>
    /// <param name="retryMissing">Relance tout de suite la recherche des jeux auxquels il manque un visuel
    /// (sinon elle n'est retentée qu'une fois par jour).</param>
    public async Task EnrichAsync(IReadOnlyList<Game> games, IProgress<EnrichProgress>? progress, CancellationToken ct, bool retryMissing = false)
    {
        await QuickImagesAsync(games, progress, ct);

        var groups = games.GroupBy(g => g.MetadataKey).Where(g => NeedsWork(g, retryMissing)).ToList();
        var done = 0;
        progress?.Report(new EnrichProgress("Fiches de jeux", 0, groups.Count, null));
        if (groups.Count == 0) return;

        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (group, token) =>
        {
            try
            {
                progress?.Report(new EnrichProgress("Fiches de jeux", Volatile.Read(ref done), groups.Count, group.First().DisplayTitle));
                await EnrichGroupAsync(group.Key, group.ToList(), token, retryMissing);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Métadonnées non récupérées pour {group.First().Title}", ex);
            }
            progress?.Report(new EnrichProgress("Fiches de jeux", Interlocked.Increment(ref done), groups.Count, null));
        });
    }

    /// <summary>Passe 1 : télécharge tout de suite les visuels dont l'adresse est déjà connue.</summary>
    private async Task QuickImagesAsync(IReadOnlyList<Game> games, IProgress<EnrichProgress>? progress, CancellationToken ct)
    {
        var groups = games.GroupBy(g => g.MetadataKey)
            .Select(g => (Key: g.Key, Games: g.ToList(), Meta: Cached(g.Key)))
            .Where(g => g.Games.Any(x => x.CoverPath is null || x.HeroPath is null || x.LogoPath is null))
            .ToList();
        if (groups.Count == 0) return;

        var done = 0;
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (g, token) =>
        {
            var appId = g.Games.Select(x => x.SteamAppId).FirstOrDefault(i => i is not null) ?? g.Meta?.SteamAppId;
            IEnumerable<string?> Candidates(Func<Game, string?> platform, IEnumerable<string>? known, Func<int, IEnumerable<string>> steam)
                => g.Games.Select(platform)
                    .Concat(known ?? [])
                    .Concat(appId is int id ? steam(id) : []);

            var cover = await _images.GetAsync(g.Key, "cover", Candidates(x => x.CoverUrl, g.Meta?.Covers, SteamStore.CoverUrls), token);
            var hero = await _images.GetAsync(g.Key, "hero", Candidates(x => x.HeroUrl, g.Meta?.Heroes, SteamStore.HeroUrls), token);
            var logo = await _images.GetAsync(g.Key, "logo", Candidates(x => x.LogoUrl, g.Meta?.Logos, SteamStore.LogoUrls), token);
            foreach (var game in g.Games)
            {
                game.CoverPath ??= cover;
                game.HeroPath ??= hero;
                game.LogoPath ??= logo;
            }
            var n = Interlocked.Increment(ref done);
            progress?.Report(new EnrichProgress("Visuels", n, groups.Count, g.Games[0].DisplayTitle));
        });
    }

    private static bool MissingImages(IEnumerable<Game> games)
        => games.Any(g => g.CoverPath is null || g.HeroPath is null || g.LogoPath is null);

    private bool NeedsWork(IGrouping<string, Game> group, bool retryMissing)
    {
        var meta = Cached(group.Key);
        if (meta is null || MissingSources(meta).Any()) return true;
        if (retryMissing && MissingImages(group)) return true;
        if (meta.NotFound) return DateTime.Now - meta.FetchedAt > RetryNotFound;
        // Visuel manquant : nouveau téléchargement à chaque démarrage (nouvelle recherche une fois par jour).
        return MissingImages(group);
    }

    private IEnumerable<string> MissingSources(GameMetadata meta)
    {
        // Jeu non retrouvé sur Steam : une nouvelle chance (recherche par titre améliorée, échec passager).
        if (!meta.Attempted.Contains("Steam") || (meta.SteamAppId is null && !meta.Attempted.Contains("SteamRetry"))) yield return "Steam";
        if (_igdb.IsConfigured && !meta.Attempted.Contains("IGDB")) yield return "IGDB";
        if (_grid.IsConfigured && !meta.Attempted.Contains("SteamGridDB")) yield return "SteamGridDB";
    }

    private async Task EnrichGroupAsync(string key, List<Game> games, CancellationToken ct, bool retryMissing)
    {
        var first = games[0];
        var previous = Cached(key);
        var meta = previous;
        if (meta is null || meta.NotFound || DateTime.Now - meta.FetchedAt > RetryNotFound) meta = new GameMetadata();
        // Visuel toujours manquant : on refait la recherche complète (forcée, ou au plus une fois par jour).
        else if (MissingImages(games) && (retryMissing || DateTime.Now - meta.FetchedAt > TimeSpan.FromDays(1))) meta = new GameMetadata();

        if (first.Platform == Platform.Ankama) meta = AnkamaCatalog.Complete(first.Title, meta)!;

        var missing = MissingSources(meta).ToList();
        if (missing.Contains("Steam"))
        {
            var appId = games.Select(g => g.SteamAppId).FirstOrDefault(i => i is not null)
                        ?? await _steam.FindAppIdAsync(first.Title, ct);
            if (appId is int id) await _steam.FillAsync(id, meta, ct);
            meta.Attempted.Add("Steam");
            meta.Attempted.Add("SteamRetry");
        }
        if (missing.Contains("IGDB"))
        {
            await _igdb.FillAsync(first.Title, meta, ct);
            meta.Attempted.Add("IGDB");
        }
        if (missing.Contains("SteamGridDB"))
        {
            await _grid.FillAsync(first.Title, meta.SteamAppId ?? first.SteamAppId, meta, ct);
            meta.Attempted.Add("SteamGridDB");
        }

        meta.NotFound = meta.Sources.Count == 0 && games.All(g => g.CoverUrl is null);
        // Nouvelle recherche infructueuse : on garde la fiche précédente plutôt que de la perdre.
        if (meta.NotFound && previous is { NotFound: false }) meta = previous;
        meta.FetchedAt = DateTime.Now;
        _cache[key] = meta;
        Json.Save(FileFor(key), meta);

        // Visuels : ceux fournis par la plateforme passent en premier.
        var cover = await _images.GetAsync(key, "cover", games.Select(g => g.CoverUrl).Concat(meta.Covers), ct);
        var hero = await _images.GetAsync(key, "hero", games.Select(g => g.HeroUrl).Concat(meta.Heroes), ct);
        var logo = await _images.GetAsync(key, "logo", games.Select(g => g.LogoUrl).Concat(meta.Logos), ct);

        foreach (var g in games)
        {
            if (!meta.NotFound) g.Meta = meta;
            g.CoverPath = cover;
            g.HeroPath = hero;
            g.LogoPath = logo;
        }
    }

    /// <summary>Efface la fiche d'un jeu pour forcer une nouvelle recherche.</summary>
    public void Forget(Game game)
    {
        var key = game.MetadataKey;
        _cache.TryRemove(key, out _);
        foreach (var path in new[] { FileFor(key), ImageCache.PathFor(key, "cover"), ImageCache.PathFor(key, "hero"), ImageCache.PathFor(key, "logo") })
            try { File.Delete(path); } catch { }
        game.Meta = null;
        game.CoverPath = game.HeroPath = game.LogoPath = null;
    }
}
