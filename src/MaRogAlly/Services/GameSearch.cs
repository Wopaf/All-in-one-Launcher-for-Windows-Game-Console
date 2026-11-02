using System.Net.Http;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Metadata;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>
/// Résultat d'une recherche (onglet Découverte) : un jeu de la bibliothèque, du magasin Steam ou du catalogue GOG.
/// </summary>
public sealed class SearchHit : ObservableObject
{
    public string Title { get; init; } = "";
    public PlatformInfo Platform { get; init; } = PlatformInfo.Of(Models.Platform.Steam);
    /// <summary>Jeu déjà dans la bibliothèque (ouvre sa fiche).</summary>
    public Game? Owned { get; init; }
    /// <summary>Jeu du magasin Steam (ouvre sa fiche magasin).</summary>
    public DiscoverGame? Steam { get; init; }
    /// <summary>Page du jeu dans une autre boutique (GOG).</summary>
    public string? StoreUrl { get; init; }
    public string? Price { get; init; }
    public bool IsDiscounted { get; init; }

    private string? _coverPath, _heroPath;
    public string? CoverPath { get => _coverPath; set => Set(ref _coverPath, value); }
    public string? HeroPath { get => _heroPath; set => Set(ref _heroPath, value); }

    /// <summary>Badge de la jaquette : « Installé » / « Possédé », sinon le prix.</summary>
    public string? Badge => Owned is { } g ? (g.IsInstalled ? "Installé" : "Possédé") : Price;
    public bool IsOwned => Owned is not null;
    /// <summary>Ligne d'information sous les résultats pour le jeu sélectionné.</summary>
    public string Info => string.Join(" · ", new[]
    {
        Owned is { } g ? $"Dans ta bibliothèque {g.PlatformInfo.Name}" : $"Sur {Platform.Name}",
        Owned is null ? Price : null,
        Steam?.ReleaseText ?? Owned?.ReleaseYear?.ToString(),
        Steam?.ReviewText,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Recherche d'un jeu sur plusieurs boutiques à la fois (Steam et GOG).</summary>
public sealed class GameSearch(HttpClient http, DiscoverService discover)
{
    private readonly ImageCache _images = new(http);

    public Task<List<DiscoverGame>> SteamAsync(string term, CancellationToken ct) => discover.FindAsync(term, ct);

    /// <summary>Catalogue public de GOG ; seuls les titres qui contiennent tous les mots cherchés sont gardés.</summary>
    public async Task<List<SearchHit>> GogAsync(string term, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        try
        {
            var url = "https://catalog.gog.com/v1/catalog?limit=24&query=like:" + Uri.EscapeDataString(term)
                      + "&order=desc:score&productType=in:game,pack&countryCode=FR&locale=fr-FR&currencyCode=EUR";
            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return hits;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return hits;

            var words = Text.Normalize(term).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in products.EnumerateArray())
            {
                var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (title is null) continue;
                var normalized = Text.Normalize(title);
                if (!words.All(normalized.Contains)) continue;
                string? price = null;
                var discounted = false;
                if (p.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    var final = pr.TryGetProperty("finalMoney", out var fm) && fm.TryGetProperty("amount", out var a) ? a.GetString() : null;
                    price = final is "0.00" ? "Gratuit" : final is not null ? final.Replace('.', ',') + " €" : null;
                    discounted = pr.TryGetProperty("discount", out var d) && d.GetString() is { Length: > 0 } || final is "0.00";
                }
                var hit = new SearchHit
                {
                    Title = title,
                    Platform = PlatformInfo.Of(Platform.Gog),
                    StoreUrl = p.TryGetProperty("storeLink", out var link) ? link.GetString() : null,
                    Price = price,
                    IsDiscounted = discounted,
                };
                var id = p.TryGetProperty("id", out var i) ? i.GetString() : null;
                var cover = p.TryGetProperty("coverVertical", out var cv) ? cv.GetString() : null;
                var hero = p.TryGetProperty("coverHorizontal", out var ch) ? ch.GetString() : null;
                if (id is not null) _ = LoadImagesAsync(hit, $"gogstore-{id}", cover, hero, ct);
                hits.Add(hit);
                if (hits.Count == 10) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"Recherche GOG « {term} »", ex); }
        return hits;
    }

    private async Task LoadImagesAsync(SearchHit hit, string key, string? cover, string? hero, CancellationToken ct)
    {
        try
        {
            hit.CoverPath = await _images.GetAsync(key, "cover", [cover], ct);
            hit.HeroPath = await _images.GetAsync(key, "hero", [hero], ct);
        }
        catch (OperationCanceledException) { }
    }

}
