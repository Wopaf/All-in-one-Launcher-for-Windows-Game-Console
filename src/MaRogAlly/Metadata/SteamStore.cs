using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Metadata;

/// <summary>Magasin Steam : gratuit, sans clé, descriptions en français. Limité à ~200 requêtes / 5 min.</summary>
public sealed class SteamStore(HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1300);

    private static readonly string[] Cdns =
    [
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps",
        "https://cdn.cloudflare.steamstatic.com/steam/apps",
    ];

    public static IEnumerable<string> CoverUrls(int id) => Cdns.SelectMany(c => new[] { $"{c}/{id}/library_600x900_2x.jpg", $"{c}/{id}/library_600x900.jpg" });
    /// <summary>Capsule large : existe pour les jeux trop anciens pour avoir une jaquette verticale.</summary>
    public static IEnumerable<string> HeaderUrls(int id) => Cdns.Select(c => $"{c}/{id}/header.jpg");
    public static IEnumerable<string> HeroUrls(int id) => Cdns.Select(c => $"{c}/{id}/library_hero.jpg");
    public static IEnumerable<string> LogoUrls(int id) => Cdns.Select(c => $"{c}/{id}/logo.png");

    /// <summary>Retrouve l'identifiant Steam d'un jeu d'une autre plateforme à partir de son titre.</summary>
    public async Task<int?> FindAppIdAsync(string title, CancellationToken ct)
    {
        // Titre exact d'abord, puis des variantes simplifiées (« KillingFloor2Beta » → « Killing Floor 2 »).
        foreach (var variant in Text.SearchVariants(title))
            if (await SearchAsync(variant, ct) is int id) return id;
        return null;
    }

    private async Task<int?> SearchAsync(string clean, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(clean)}&l=french&cc=FR", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items)) return null;

        var wanted = Text.Normalize(clean);
        var candidates = items.EnumerateArray()
            .Select(i => (Id: i.GetProperty("id").GetInt32(), Name: Text.Normalize(i.GetProperty("name").GetString())))
            .ToList();

        var exact = candidates.FirstOrDefault(c => c.Name == wanted);
        if (exact.Id != 0) return exact.Id;

        // Tolère « Edition », « Remastered »… si le premier résultat contient clairement le titre.
        var first = candidates.FirstOrDefault();
        if (first.Id != 0 && wanted.Length >= 4 && (first.Name.StartsWith(wanted) || wanted.StartsWith(first.Name))
            && Math.Abs(first.Name.Length - wanted.Length) <= 20)
            return first.Id;
        return null;
    }

    /// <summary>Complète la fiche à partir de la page magasin (description, genres, date, studio, note Metacritic).</summary>
    public async Task<bool> FillAsync(int appId, GameMetadata meta, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=french&cc=FR", ct);
        if (doc is null || !doc.RootElement.TryGetProperty(appId.ToString(), out var root)) return false;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean() || !root.TryGetProperty("data", out var d)) return false;

        meta.SteamAppId = appId;
        meta.Title ??= Str(d, "name");
        var about = Text.StripHtml(Str(d, "about_the_game") ?? Str(d, "detailed_description"));
        meta.Description ??= about.Length > 40 ? about : Text.StripHtml(Str(d, "short_description"));
        if (meta.Genres.Count == 0 && d.TryGetProperty("genres", out var genres))
            meta.Genres = genres.EnumerateArray().Select(g => Str(g, "description")).OfType<string>().ToList();
        meta.Developer ??= First(d, "developers");
        meta.Publisher ??= First(d, "publishers");
        if (meta.ReleaseDate is null && d.TryGetProperty("release_date", out var rd) && Str(rd, "date") is { } date
            && DateTime.TryParse(date, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var parsed))
            meta.ReleaseDate = parsed;
        if (meta.Rating is null && d.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var score))
            meta.Rating = score.GetDouble();

        meta.Covers.AddRange(CoverUrls(appId));
        meta.Heroes.AddRange(HeroUrls(appId));
        if (Str(d, "background_raw") is { } bg) meta.Heroes.Add(bg);
        meta.Logos.AddRange(LogoUrls(appId));
        if (Str(d, "header_image") is { } header) meta.Covers.Add(header);
        if (d.TryGetProperty("screenshots", out var shots))
            meta.Screenshots.AddRange(shots.EnumerateArray().Select(s => Str(s, "path_full")).OfType<string>().Take(8));
        meta.Sources.Add("Steam");
        return true;
    }

    /// <summary>Taille sur la ligne de stockage de la configuration requise (« Storage: 50 GB available space »).</summary>
    private static readonly Regex StorageLine = new(
        @"(storage|hard drive|hard disk|disk space|hdd|ssd)[^0-9]{0,40}?(\d+(?:[.,]\d+)?)\s*(tb|gb|mb)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Espace disque demandé par la configuration requise de la page Steam (en octets) ; 0 si la page n'en dit rien,
    /// null si le magasin ne répond pas. Recommandée d'abord, sinon minimale : la taille du jeu est la même, la recommandée est plus souvent à jour.
    /// </summary>
    public async Task<long?> RequiredStorageAsync(int appId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=FR", ct);
        if (doc is null || !doc.RootElement.TryGetProperty(appId.ToString(), out var root)) return null;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean() || !root.TryGetProperty("data", out var d)) return 0;
        // Sans configuration requise, Steam renvoie un tableau vide au lieu d'un objet.
        if (!d.TryGetProperty("pc_requirements", out var req) || req.ValueKind != JsonValueKind.Object) return 0;
        foreach (var level in new[] { "recommended", "minimum" })
        {
            var match = StorageLine.Match(Text.StripHtml(Str(req, level)));
            if (!match.Success || !double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) continue;
            var unit = match.Groups[3].Value.ToUpperInvariant() switch { "TB" => 1L << 40, "GB" => 1L << 30, _ => 1L << 20 };
            return (long)(amount * unit);
        }
        return 0;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _next = DateTime.UtcNow + Spacing;

                using var res = await http.GetAsync(url, ct);
                if (res.StatusCode == HttpStatusCode.TooManyRequests || res.StatusCode == HttpStatusCode.Forbidden)
                {
                    Log.Info("Magasin Steam : limite atteinte, pause d'une minute");
                    _next = DateTime.UtcNow + TimeSpan.FromSeconds(65);
                    continue;
                }
                if (!res.IsSuccessStatusCode) return null;
                return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? First(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            : null;
}
