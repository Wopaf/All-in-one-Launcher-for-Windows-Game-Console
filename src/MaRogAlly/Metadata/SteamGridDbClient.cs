using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Metadata;

/// <summary>
/// SteamGridDB : jaquettes, fonds (« heroes »), logos détourés. Clé gratuite sur
/// https://www.steamgriddb.com/profile/preferences/api
/// </summary>
public sealed class SteamGridDbClient(HttpClient http, AppSettings settings)
{
    private const string Api = "https://www.steamgriddb.com/api/v2";

    public bool IsConfigured => settings.HasSteamGridDb;

    public async Task<bool> FillAsync(string title, int? steamAppId, GameMetadata meta, CancellationToken ct)
    {
        if (!IsConfigured) return false;

        int? gameId = null;
        if (steamAppId is int appId)
        {
            using var g = await GetAsync($"{Api}/games/steam/{appId}", ct);
            if (g is not null && g.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id))
                gameId = id.GetInt32();
        }
        if (gameId is null)
        {
            using var s = await GetAsync($"{Api}/search/autocomplete/{Uri.EscapeDataString(Text.CleanTitle(title))}", ct);
            if (s is not null && s.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var wanted = Text.Normalize(title);
                var items = data.EnumerateArray().ToList();
                var best = items.FirstOrDefault(i => Text.Normalize(i.GetProperty("name").GetString()) == wanted);
                if (best.ValueKind == JsonValueKind.Undefined && items.Count > 0) best = items[0];
                if (best.ValueKind != JsonValueKind.Undefined) gameId = best.GetProperty("id").GetInt32();
            }
        }
        if (gameId is null) return false;

        // Placées en tête : ce sont les visuels de meilleure qualité.
        meta.Covers.InsertRange(0, await UrlsAsync($"{Api}/grids/game/{gameId}?dimensions=600x900&types=static", ct));
        meta.Heroes.InsertRange(0, await UrlsAsync($"{Api}/heroes/game/{gameId}?types=static", ct));
        meta.Logos.InsertRange(0, await UrlsAsync($"{Api}/logos/game/{gameId}?types=static", ct));
        meta.Sources.Add("SteamGridDB");
        return true;
    }

    private async Task<List<string>> UrlsAsync(string url, CancellationToken ct)
    {
        using var doc = await GetAsync(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray()
            .Select(i => i.TryGetProperty("url", out var u) ? u.GetString() : null)
            .OfType<string>()
            .Take(2)
            .ToList();
    }

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SteamGridDbKey.Trim());
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            if (res.StatusCode != System.Net.HttpStatusCode.NotFound) Log.Error($"SteamGridDB : HTTP {(int)res.StatusCode}");
            return null;
        }
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
