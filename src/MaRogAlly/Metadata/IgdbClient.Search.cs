using System.Text.Json;

namespace MaRogAlly.Metadata;

/// <summary>Jeu proposé pendant la saisie d'un nom (ajout d'un jeu à la main), quelle que soit sa plateforme.</summary>
public sealed record GameSuggestion(string Name, int? Year, string Platforms, string? CoverUrl, string? HeroUrl, int? SteamAppId)
{
    /// <summary>« Switch · 2017 », « NES, Game Boy · 1989 »…</summary>
    public string Detail => string.Join(" · ", new[] { Platforms, Year?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed partial class IgdbClient
{
    /// <summary>
    /// Recherche dans tout le catalogue IGDB : PC, consoles actuelles et rétro (NES, Game Boy, PlayStation…).
    /// Jeux principaux seulement (remakes, remasters et portages compris), pas les DLC, packs ni éditions.
    /// </summary>
    public async Task<List<GameSuggestion>> SearchAsync(string term, CancellationToken ct)
    {
        if (!IsConfigured) return [];
        var clean = term.Replace("\"", "").Trim();
        const string fields = "name,first_release_date,cover.image_id,artworks.image_id,screenshots.image_id,platforms.abbreviation,platforms.name,version_parent";
        // Type de jeu et liens externes : champs qui ont changé de nom chez IGDB, d'où une seconde demande sans eux.
        using var doc = await PostAsync("games", $"search \"{clean}\"; fields {fields},game_type,category,external_games.uid,external_games.category,websites.url; limit 20;", ct)
                        ?? await PostAsync("games", $"search \"{clean}\"; fields {fields},category; limit 20;", ct);
        var found = new List<GameSuggestion>();
        if (doc?.RootElement.ValueKind != JsonValueKind.Array) return found;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (Str(e, "name") is not { } name) continue;
            var kind = Int(e, "game_type") ?? Int(e, "category");
            if (kind is not (null or 0 or 4 or 8 or 9 or 10 or 11) || e.TryGetProperty("version_parent", out _)) continue;
            var year = e.TryGetProperty("first_release_date", out var frd) && frd.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).Year : (int?)null;
            var cover = e.TryGetProperty("cover", out var c) && Str(c, "image_id") is { } coverId ? ImageUrl(coverId, "t_cover_big_2x") : null;
            var hero = ImageIds(e, "artworks").Concat(ImageIds(e, "screenshots")).Select(i => ImageUrl(i, "t_1080p")).FirstOrDefault();
            found.Add(new GameSuggestion(name, year, ShortPlatforms(e), cover, hero, SteamIdOf(e)));
        }
        return found.Take(10).ToList();
    }

    /// <summary>Plateformes en abréviations (« Switch », « NES », « PS1 »), trois au plus.</summary>
    private static string ShortPlatforms(JsonElement e)
    {
        if (!e.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array) return "";
        var names = platforms.EnumerateArray()
            .Select(p => Str(p, "abbreviation") ?? Str(p, "name"))
            .OfType<string>()
            .Select(n => n switch { "Win" or "PC" => "PC", "Switch" or "NSW" => "Switch", "PS" => "PS1", _ => n })
            .Distinct()
            .ToList();
        return names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))}…";
    }
}
