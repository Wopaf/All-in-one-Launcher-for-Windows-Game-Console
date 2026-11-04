using System.Net.Http;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Scanners;

namespace MaRogAlly.Services;

public sealed record PlatformStatus(PlatformInfo Info, bool ClientInstalled, int Installed, int Owned)
{
    public string Summary => (ClientInstalled, Installed, Owned) switch
    {
        (false, 0, 0) => "Lanceur non installé",
        (false, _, _) => $"Lanceur non installé · {Owned} jeux possédés",
        (true, 0, 0) => "Aucun jeu détecté",
        _ => $"{Installed} installé{(Installed > 1 ? "s" : "")} · {Owned} dans la bibliothèque",
    };
}

/// <summary>Scanne toutes les plateformes, fusionne installés et possédés, et garde le tout en cache.</summary>
public sealed class LibraryService(AppSettings settings, HttpClient http)
{
    private static readonly GameScanner[] Scanners =
    [
        new SteamScanner(), new EpicScanner(), new GogScanner(), new EaScanner(), new UbisoftScanner(),
        new BattleNetScanner(), new XboxScanner(), new AmazonScanner(), new RiotScanner(), new ItchScanner(),
        new RockstarScanner(), new AnkamaScanner(), new MinecraftScanner(), new RsiScanner(), new ManualScanner(),
    ];

    private Dictionary<string, GameUserData> _userData = Json.Load<Dictionary<string, GameUserData>>(AppPaths.UserData) ?? [];

    /// <summary>Lanceurs installés sur cette machine et que l'on sait ouvrir.</summary>
    public static List<PlatformInfo> InstalledLaunchers()
        => Scanners.Where(s =>
            {
                try { return s.Platform != Platform.Manual && s.IsClientInstalled(); }
                catch { return false; }
            })
            .Select(s => PlatformInfo.Of(s.Platform))
            .Where(p => LauncherWarmup.Command(p) is not null)
            .ToList();

    /// <summary>Bibliothèque du dernier scan, pour un affichage immédiat au démarrage.</summary>
    public List<Game> LoadCached()
    {
        var games = Json.Load<List<Game>>(AppPaths.Library) ?? [];
        foreach (var g in games) ApplyUserData(g);
        return games;
    }

    /// <param name="online">Faux : disque seulement, pour refléter sans attendre une installation ou une désinstallation
    /// (les jeux possédés sont repris du scan précédent, un jeu désinstallé y reste comme non installé).</param>
    public async Task<(List<Game> Games, List<PlatformStatus> Platforms)> ScanAsync(IReadOnlyList<Game> previous, CancellationToken ct, bool online = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var galaxy = await Task.Run(GalaxyLibrary.Load, ct);
        var ctx = new ScanContext(settings, http, galaxy);

        var results = await Task.WhenAll(Scanners.Select(s => Task.Run(() => ScanPlatformAsync(s, ctx, online, ct), ct)));

        var byId = new Dictionary<string, Game>();
        var prevById = previous.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        var statuses = new List<PlatformStatus>();

        foreach (var r in results)
        {
            foreach (var g in r.Installed) byId.TryAdd(g.Id, g);

            // Hors ligne ou API en erreur : on garde les jeux possédés connus du scan précédent.
            var owned = r.Owned ?? previous.Where(p => p.Platform == r.Scanner.Platform && (!online || !p.IsInstalled)).ToList();
            foreach (var o in owned)
            {
                // GOG Galaxy n'a pas toujours le même identifiant que le lanceur (Hearthstone : hs_beta / WTCG) :
                // à titre identique sur la même plateforme, c'est le jeu installé.
                var title = Text.Normalize(o.Title);
                var installed = byId.GetValueOrDefault(o.Id)
                                ?? (title.Length == 0 ? null : r.Installed.FirstOrDefault(g => Text.Normalize(g.Title) == title));
                if (installed is not null)
                {
                    if (installed.Id != o.Id && galaxy.PlayTime(o.Platform, o.PlatformId) is { } time)
                    {
                        installed.PlaytimeMinutes ??= time.Minutes;
                        installed.PlatformLastPlayed ??= time.Last;
                    }
                    installed.PlaytimeMinutes ??= o.PlaytimeMinutes;
                    installed.CoverUrl ??= o.CoverUrl;
                    installed.HeroUrl ??= o.HeroUrl;
                    installed.LogoUrl ??= o.LogoUrl;
                    installed.InstallUri ??= o.InstallUri;
                    installed.PlatformLastPlayed ??= o.PlatformLastPlayed;
                }
                else
                {
                    o.IsInstalled = false;
                    byId.TryAdd(o.Id, o);
                }
            }

            var mine = byId.Values.Where(g => g.Platform == r.Scanner.Platform).ToList();
            statuses.Add(new PlatformStatus(PlatformInfo.Of(r.Scanner.Platform), r.ClientInstalled,
                mine.Count(g => g.IsInstalled), mine.Count));
        }

        var games = RemoveDuplicateInstalls(byId.Values.ToList());
        ApplyPlayTimes(games, galaxy);
        foreach (var g in games)
        {
            if (prevById.TryGetValue(g.Id, out var p)) g.FirstSeen = p.FirstSeen;
            ApplyUserData(g);
        }

        Json.Save(AppPaths.Library, games);
        Log.Info($"Scan terminé en {sw.ElapsedMilliseconds} ms : {games.Count(g => g.IsInstalled)} installés, {games.Count} au total");
        return (games, statuses.OrderBy(s => (int)s.Info.Id).ToList());
    }

    /// <summary>
    /// Identifiants des jeux installés, lus sur le disque seulement (sans les bibliothèques en ligne) :
    /// assez rapide pour repérer régulièrement une installation ou une désinstallation.
    /// </summary>
    public async Task<HashSet<string>> InstalledIdsAsync(CancellationToken ct)
    {
        // Seuls les jeux possédés viennent de la base Galaxy : inutile de la recopier à chaque lecture.
        var ctx = new ScanContext(settings, http, GalaxyLibrary.Empty);
        var lists = await Task.WhenAll(Scanners.Select(s => Task.Run(() =>
        {
            try { return s.ScanInstalled(ctx).ToList(); }
            catch { return new List<Game>(); }
        }, ct)));
        var byId = new Dictionary<string, Game>();
        foreach (var g in lists.SelectMany(l => l)) byId.TryAdd(g.Id, g);
        return RemoveDuplicateInstalls(byId.Values.ToList()).Select(g => g.Id).ToHashSet();
    }

    /// <summary>
    /// Temps de jeu et dernière partie lus localement (Steam, GOG Galaxy), sans clé d'API.
    /// On garde toujours la plus grande valeur connue (l'API Steam peut aussi en fournir).
    /// </summary>
    private static void ApplyPlayTimes(List<Game> games, GalaxyLibrary galaxy)
    {
        var steam = SteamScanner.LocalPlayTimes();
        foreach (var g in games)
        {
            var sources = new List<(int Minutes, DateTime? Last)>();
            if (g.Platform == Platform.Steam && steam.TryGetValue(g.PlatformId, out var s)) sources.Add(s);
            if (galaxy.PlayTime(g.Platform, g.PlatformId) is { } gx) sources.Add(gx);
            foreach (var (minutes, last) in sources)
            {
                if (minutes > (g.PlaytimeMinutes ?? 0)) g.PlaytimeMinutes = minutes;
                if (last is { } l && (g.PlatformLastPlayed is null || l > g.PlatformLastPlayed)) g.PlatformLastPlayed = l;
            }
        }
    }

    private sealed record PlatformResult(GameScanner Scanner, bool ClientInstalled, List<Game> Installed, List<Game>? Owned);

    private static async Task<PlatformResult> ScanPlatformAsync(GameScanner scanner, ScanContext ctx, bool online, CancellationToken ct)
    {
        bool client = false;
        List<Game> installed = [];
        List<Game>? owned = null;
        try { client = scanner.IsClientInstalled(); } catch (Exception ex) { Log.Error($"{scanner.Platform} : détection du lanceur", ex); }
        try { installed = scanner.ScanInstalled(ctx).ToList(); } catch (Exception ex) { Log.Error($"{scanner.Platform} : jeux installés", ex); }
        if (online) try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            owned = (await scanner.ScanOwnedAsync(ctx, timeout.Token)).ToList();
        }
        catch (Exception ex) { Log.Error($"{scanner.Platform} : bibliothèque en ligne", ex); }
        return new PlatformResult(scanner, client || installed.Count > 0, installed, owned);
    }

    /// <summary>
    /// Un jeu Steam peut aussi s'enregistrer chez Rockstar ou EA : si un dossier d'installation est
    /// déjà couvert par une plateforme prioritaire (ordre de l'énumération), on écarte le doublon.
    /// </summary>
    private static List<Game> RemoveDuplicateInstalls(List<Game> games)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(p.Replace('/', '\\')) + "\\";
        var kept = new List<Game>();
        foreach (var g in games.OrderBy(g => (int)g.Platform))
        {
            if (g.IsInstalled && g.InstallDir is { Length: > 3 } dir && g.Platform != Platform.Manual)
            {
                var d = Norm(dir);
                if (kept.Any(k => k.IsInstalled && k.Platform != g.Platform && k.InstallDir is { Length: > 3 } kd
                                  && d.StartsWith(Norm(kd), StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            kept.Add(g);
        }
        return kept;
    }

    // ---- Préférences par jeu ----

    private void ApplyUserData(Game g)
    {
        if (!_userData.TryGetValue(g.Id, out var u)) return;
        g.IsHidden = u.Hidden;
        g.LastPlayed = u.LastPlayed;
        g.LaunchCount = u.LaunchCount;
    }

    public void SaveUserData(Game g)
    {
        _userData[g.Id] = new GameUserData
        {
            Hidden = g.IsHidden,
            LastPlayed = g.LastPlayed,
            LaunchCount = g.LaunchCount,
        };
        Json.Save(AppPaths.UserData, _userData);
    }
}
