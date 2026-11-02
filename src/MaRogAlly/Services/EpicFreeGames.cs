using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Metadata;

namespace MaRogAlly.Services;

/// <summary>Jeu offert (ou bientôt offert) par l'Epic Games Store.</summary>
public sealed class FreeGame : ObservableObject
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Seller { get; init; }
    public string? OriginalPrice { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool IsCurrent { get; init; }
    public string? Slug { get; init; }
    public List<string> WideUrls { get; init; } = [];

    private string? _imagePath;
    public string? ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }

    private bool _isOwned;
    public bool IsOwned { get => _isOwned; set { if (Set(ref _isOwned, value)) OnPropertyChanged(nameof(Badge)); } }

    public string WebUrl => Slug is null ? "https://store.epicgames.com/fr/free-games" : $"https://store.epicgames.com/fr/p/{Slug}";
    public string LauncherUrl => Slug is null ? "com.epicgames.launcher://store/free-games" : $"com.epicgames.launcher://store/p/{Slug}";

    /// <summary>« OBTENU » quand le jeu est déjà dans la bibliothèque Epic.</summary>
    public string Badge => IsOwned ? "OBTENU" : IsCurrent ? "GRATUIT" : "BIENTÔT";

    /// <summary>« Jusqu'au jeudi 5 novembre à 17:00 » ou « À partir du jeudi 29 octobre à 16:00 ».</summary>
    public string DateText => IsCurrent
        ? $"Gratuit jusqu'au {End.ToString("dddd d MMMM 'à' HH:mm", Fr)}"
        : $"Gratuit à partir du {Start.ToString("dddd d MMMM 'à' HH:mm", Fr)}";

    /// <summary>Compte à rebours court pour les cartes.</summary>
    public string Countdown
    {
        get
        {
            var span = (IsCurrent ? End : Start) - DateTime.Now;
            if (span <= TimeSpan.Zero) return IsCurrent ? "Terminé" : "Disponible";
            var text = span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min"
                : span.TotalDays < 1 ? $"{(int)span.TotalHours} h"
                : $"{(int)span.TotalDays} j {span.Hours} h";
            return IsCurrent ? $"Se termine dans {text}" : $"Dans {text}";
        }
    }

    public void RefreshCountdown() => OnPropertyChanged(nameof(Countdown));
}

/// <summary>
/// Jeux offerts chaque semaine par Epic. Point d'accès public mais non documenté
/// (store-site-backend-static) : on essaie plusieurs hôtes et on reste silencieux en cas d'échec.
/// </summary>
public sealed class EpicFreeGamesService(HttpClient http)
{
    private static readonly string[] Hosts =
    [
        "https://store-site-backend-static.ak.epicgames.com",
        "https://store-site-backend-static-ipv4.ak.epicgames.com",
        "https://store-site-backend-static.epicgames.com",
    ];

    private readonly ImageCache _images = new(http);

    public async Task<List<FreeGame>> FetchAsync(CancellationToken ct)
    {
        foreach (var host in Hosts)
        {
            try
            {
                using var res = await http.GetAsync($"{host}/freeGamesPromotions?locale=fr&country=FR&allowCountries=FR", ct);
                if (!res.IsSuccessStatusCode) continue;
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var games = Parse(doc.RootElement);
                Log.Info($"Epic : {games.Count(g => g.IsCurrent)} jeu(x) offert(s), {games.Count(g => !g.IsCurrent)} à venir");
                return games;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Jeux offerts Epic indisponibles ({host})", ex);
            }
        }
        return [];
    }

    /// <summary>Télécharge l'image large de chaque jeu (mise en cache sur disque).</summary>
    public async Task LoadImagesAsync(IEnumerable<FreeGame> games, CancellationToken ct)
    {
        foreach (var g in games)
            g.ImagePath = await _images.GetAsync($"epicfree-{g.Id}", "wide", g.WideUrls, ct);
    }

    private static List<FreeGame> Parse(JsonElement root)
    {
        var list = new List<FreeGame>();
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Catalog", out var cat)
            || !cat.TryGetProperty("searchStore", out var store)
            || !store.TryGetProperty("elements", out var elements)) return list;

        var now = DateTime.UtcNow;
        foreach (var e in elements.EnumerateArray())
        {
            if (!e.TryGetProperty("promotions", out var promos) || promos.ValueKind != JsonValueKind.Object) continue;

            // Une offre « gratuite » = remise de 100 % (discountPercentage == 0 dans le format Epic).
            var current = FreeOffers(promos, "promotionalOffers").Where(o => o.Start <= now && now < o.End)
                .Select(o => ((DateTime Start, DateTime End)?)o).FirstOrDefault();
            var upcoming = FreeOffers(promos, "upcomingPromotionalOffers").Where(o => o.Start > now).OrderBy(o => o.Start)
                .Select(o => ((DateTime Start, DateTime End)?)o).FirstOrDefault();
            if ((current ?? upcoming) is not { } offer) continue;
            var isCurrent = current is not null;

            list.Add(new FreeGame
            {
                Id = Str(e, "id") ?? Guid.NewGuid().ToString("N"),
                Title = Str(e, "title") ?? "Jeu mystère",
                Description = Str(e, "description") ?? "",
                Seller = e.TryGetProperty("seller", out var s) ? Str(s, "name") : null,
                OriginalPrice = e.TryGetProperty("price", out var p) && p.TryGetProperty("totalPrice", out var tp)
                                && tp.TryGetProperty("fmtPrice", out var fp) ? Str(fp, "originalPrice") : null,
                Start = offer.Start.ToLocalTime(),
                End = offer.End.ToLocalTime(),
                IsCurrent = isCurrent,
                Slug = Slug(e),
                WideUrls = Images(e, "OfferImageWide", "DieselStoreFrontWide", "featuredMedia", "OfferImageTall", "Thumbnail"),
            });
        }
        return list.OrderByDescending(g => g.IsCurrent).ThenBy(g => g.IsCurrent ? g.End : g.Start).ToList();
    }

    private static IEnumerable<(DateTime Start, DateTime End)> FreeOffers(JsonElement promos, string name)
    {
        if (!promos.TryGetProperty(name, out var groups) || groups.ValueKind != JsonValueKind.Array) yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("promotionalOffers", out var offers) || offers.ValueKind != JsonValueKind.Array) continue;
            foreach (var o in offers.EnumerateArray())
            {
                if (!o.TryGetProperty("discountSetting", out var ds) || !ds.TryGetProperty("discountPercentage", out var pct)
                    || pct.GetInt32() != 0) continue;
                if (DateTime.TryParse(Str(o, "startDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var start)
                    && DateTime.TryParse(Str(o, "endDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var end))
                    yield return (DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc));
            }
        }
    }

    private static string? Slug(JsonElement e)
    {
        foreach (var prop in new[] { "offerMappings", "catalogNs" })
        {
            if (!e.TryGetProperty(prop, out var v)) continue;
            var arr = prop == "catalogNs" && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("mappings", out var m) ? m : v;
            if (arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
                if (Str(item, "pageSlug") is { Length: > 0 } slug) return slug;
        }
        return Str(e, "productSlug")?.Replace("/home", "") is { Length: > 0 } ps ? ps : null;
    }

    private static List<string> Images(JsonElement e, params string[] types)
    {
        if (!e.TryGetProperty("keyImages", out var imgs) || imgs.ValueKind != JsonValueKind.Array) return [];
        var all = imgs.EnumerateArray().Select(i => (Type: Str(i, "type"), Url: Str(i, "url"))).ToList();
        return types.SelectMany(t => all.Where(i => i.Type == t).Select(i => i.Url)).OfType<string>().Distinct().ToList();
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
