using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Metadata;

/// <summary>
/// IGDB (base de données de jeux de Twitch) : l'équivalent de TMDB pour le jeu vidéo.
/// Identifiants gratuits sur https://dev.twitch.tv/console (application « Confidential »).
/// </summary>
public sealed partial class IgdbClient(HttpClient http, AppSettings settings)
{
    private string? _token;
    private DateTime _tokenExpiry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public bool IsConfigured => settings.HasIgdb;

    public async Task<bool> FillAsync(string title, GameMetadata meta, CancellationToken ct)
    {
        if (!IsConfigured) return false;
        var clean = Text.CleanTitle(title).Replace("\"", "");
        var body = $"""
            search "{clean}";
            fields name,summary,storyline,total_rating,aggregated_rating,rating,first_release_date,
                   genres.name,themes.name,cover.image_id,artworks.image_id,screenshots.image_id,
                   involved_companies.developer,involved_companies.publisher,involved_companies.company.name;
            limit 8;
            """;

        using var doc = await PostAsync("games", body, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return false;

        var wanted = Text.Normalize(clean);
        var results = doc.RootElement.EnumerateArray().ToList();
        if (results.Count == 0) return false;
        var best = results.FirstOrDefault(r => Text.Normalize(Str(r, "name")) == wanted);
        if (best.ValueKind == JsonValueKind.Undefined) best = results[0];

        meta.Title ??= Str(best, "name");
        meta.Description ??= Str(best, "summary") ?? Str(best, "storyline");
        var rating = Num(best, "total_rating") ?? Num(best, "aggregated_rating") ?? Num(best, "rating");
        if (rating is not null) meta.Rating = Math.Round(rating.Value); // IGDB est prioritaire sur Metacritic
        if (best.TryGetProperty("first_release_date", out var frd) && frd.TryGetInt64(out var unix))
            meta.ReleaseDate ??= DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        if (meta.Genres.Count == 0)
            meta.Genres = Names(best, "genres").Concat(Names(best, "themes")).Distinct().Take(4).ToList();

        if (best.TryGetProperty("involved_companies", out var companies))
        {
            foreach (var c in companies.EnumerateArray())
            {
                var name = c.TryGetProperty("company", out var co) ? Str(co, "name") : null;
                if (name is null) continue;
                if (Bool(c, "developer")) meta.Developer ??= name;
                if (Bool(c, "publisher")) meta.Publisher ??= name;
            }
        }

        if (best.TryGetProperty("cover", out var cover) && Str(cover, "image_id") is { } coverId)
            meta.Covers.Add(ImageUrl(coverId, "t_cover_big_2x"));
        meta.Heroes.AddRange(ImageIds(best, "artworks").Select(id => ImageUrl(id, "t_1080p")));
        var shots = ImageIds(best, "screenshots").Select(id => ImageUrl(id, "t_1080p")).ToList();
        meta.Screenshots.AddRange(shots);
        meta.Heroes.AddRange(shots.Take(1));
        meta.Sources.Add("IGDB");
        return true;
    }

    private static string ImageUrl(string id, string size) => $"https://images.igdb.com/igdb/image/upload/{size}/{id}.jpg";

    private async Task<JsonDocument?> PostAsync(string endpoint, string body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // IGDB autorise 4 requêtes par seconde.
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _next = DateTime.UtcNow + TimeSpan.FromMilliseconds(280);

            var token = await TokenAsync(ct);
            if (token is null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.igdb.com/v4/{endpoint}")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            req.Headers.Add("Client-ID", settings.IgdbClientId.Trim());
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null;
            if (!res.IsSuccessStatusCode)
            {
                Log.Error($"IGDB {endpoint} : HTTP {(int)res.StatusCode}");
                return null;
            }
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiry) return _token;
        var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(settings.IgdbClientId.Trim())}" +
                  $"&client_secret={Uri.EscapeDataString(settings.IgdbClientSecret.Trim())}&grant_type=client_credentials";
        using var res = await http.PostAsync(url, null, ct);
        if (!res.IsSuccessStatusCode)
        {
            Log.Error($"IGDB : identifiants Twitch refusés (HTTP {(int)res.StatusCode})");
            return null;
        }
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        _token = Str(doc.RootElement, "access_token");
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expires - 120);
        return _token;
    }

    private static IEnumerable<string> Names(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var arr) ? arr.EnumerateArray().Select(x => Str(x, "name")).OfType<string>() : [];

    private static IEnumerable<string> ImageIds(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var arr) ? arr.EnumerateArray().Select(x => Str(x, "image_id")).OfType<string>() : [];

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
