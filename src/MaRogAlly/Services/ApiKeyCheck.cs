using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Scanners;

namespace MaRogAlly.Services;

public enum KeyState { Ok, Missing, Invalid, Unreachable }

/// <summary>Résultat de la vérification d'un service (IGDB, SteamGridDB, Steam).</summary>
public sealed record KeyResult(string Service, KeyState State, string Message);

/// <summary>
/// Vérifie les clés d'API auprès de chaque service. Une clé absente ou refusée allume une pastille
/// (bouton Clés d'API des Réglages, bouton Réglages du menu Options) ; un service injoignable ne compte pas.
/// </summary>
public static class ApiKeyCheck
{
    public static IReadOnlyList<KeyResult>? Last { get; private set; }

    /// <summary>Une clé manque ou est refusée (faux tant que rien n'a été vérifié).</summary>
    public static bool NeedsAttention => Last?.Any(r => r.State is KeyState.Missing or KeyState.Invalid) == true;

    public static event Action? Updated;

    public static async Task<IReadOnlyList<KeyResult>> RunAsync(HttpClient http, AppSettings settings, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            Safe("IGDB", () => IgdbAsync(http, settings, ct)),
            Safe("SteamGridDB", () => GridAsync(http, settings, ct)),
            Safe("Steam", () => SteamAsync(http, settings, ct)));
        Last = results;
        Log.Info("Clés d'API : " + string.Join(", ", results.Select(r => $"{r.Service} {r.State}")));
        Updated?.Invoke();
        return results;
    }

    private static async Task<KeyResult> Safe(string service, Func<Task<KeyResult>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(service, KeyState.Unreachable, "Service injoignable, vérifie ta connexion");
        }
        catch (Exception ex)
        {
            Log.Error($"Vérification de la clé {service}", ex);
            return new(service, KeyState.Unreachable, "Vérification impossible");
        }
    }

    private static async Task<KeyResult> IgdbAsync(HttpClient http, AppSettings s, CancellationToken ct)
    {
        const string name = "IGDB";
        var id = s.IgdbClientId.Trim();
        var secret = s.IgdbClientSecret.Trim();
        if (id.Length == 0 && secret.Length == 0) return new(name, KeyState.Missing, "Client ID et Client Secret non renseignés");
        if (id.Length == 0) return new(name, KeyState.Missing, "Client ID non renseigné");
        if (secret.Length == 0) return new(name, KeyState.Missing, "Client Secret non renseigné");

        var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(id)}&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";
        using var res = await http.PostAsync(url, null, ct);
        if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            return new(name, KeyState.Invalid, body.Contains("client secret", StringComparison.OrdinalIgnoreCase)
                ? "Client Secret refusé"
                : body.Contains("client id", StringComparison.OrdinalIgnoreCase) ? "Client ID refusé" : "Client ID ou Client Secret refusé");
        }
        if (!res.IsSuccessStatusCode) return new(name, KeyState.Unreachable, $"Twitch a répondu {(int)res.StatusCode}, réessaie plus tard");
        return new(name, KeyState.Ok, "Clés valides");
    }

    private static async Task<KeyResult> GridAsync(HttpClient http, AppSettings s, CancellationToken ct)
    {
        const string name = "SteamGridDB";
        var key = s.SteamGridDbKey.Trim();
        if (key.Length == 0) return new(name, KeyState.Missing, "Clé non renseignée");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.steamgriddb.com/api/v2/search/autocomplete/portal");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(name, KeyState.Invalid, "Clé refusée");
        if (!res.IsSuccessStatusCode) return new(name, KeyState.Unreachable, $"SteamGridDB a répondu {(int)res.StatusCode}, réessaie plus tard");
        return new(name, KeyState.Ok, "Clé valide");
    }

    private static async Task<KeyResult> SteamAsync(HttpClient http, AppSettings s, CancellationToken ct)
    {
        const string name = "Steam";
        var key = s.SteamApiKey.Trim();
        if (key.Length == 0) return new(name, KeyState.Missing, "Clé Web API non renseignée");
        var custom = s.SteamId.Trim();
        var steamId = custom.Length > 0 ? custom : SteamScanner.DetectSteamId();
        if (custom.Length > 0 && (custom.Length != 17 || !custom.All(char.IsAsciiDigit)))
            return new(name, KeyState.Invalid, "SteamID64 incorrect : 17 chiffres attendus");

        // Sans compte détecté : un identifiant public suffit à vérifier la clé.
        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId ?? "76561197960435530"}";
        using var res = await http.GetAsync(url, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(name, KeyState.Invalid, "Clé Web API refusée");
        if (!res.IsSuccessStatusCode) return new(name, KeyState.Unreachable, $"Steam a répondu {(int)res.StatusCode}, réessaie plus tard");
        if (steamId is null) return new(name, KeyState.Ok, "Clé valide (aucun compte Steam détecté sur cette machine)");

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var players = doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("players", out var p) ? p : default;
        if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0)
            return new(name, KeyState.Invalid, custom.Length > 0 ? "Clé valide, mais ce SteamID64 est introuvable" : "Clé valide, mais le compte détecté est introuvable");
        var player = players[0];
        var persona = player.TryGetProperty("personaname", out var n) ? n.GetString() : null;
        var visible = player.TryGetProperty("communityvisibilitystate", out var v) && v.GetInt32() == 3;
        var who = persona is null ? "" : $" (compte {persona})";
        return visible
            ? new(name, KeyState.Ok, $"Clé valide{who}")
            : new(name, KeyState.Ok, $"Clé valide{who}, mais le profil est privé : les jeux non installés n'apparaîtront pas");
    }
}
