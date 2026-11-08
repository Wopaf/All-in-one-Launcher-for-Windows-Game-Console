using System.IO;
using MaRogAlly.Core;
using MaRogAlly.Metadata;

namespace MaRogAlly.Services;

/// <summary>
/// Espace disque demandé par un jeu non installé, lu dans sa configuration requise sur le magasin Steam.
/// Gardé en cache (0 : la page Steam n'en dit rien), pour ne pas redemander à chaque ouverture de la fiche.
/// </summary>
public sealed class StoreSizes(SteamStore store)
{
    private static string CachePath => Path.Combine(AppPaths.Root, "store-sizes.json");
    private readonly Dictionary<int, long> _bytes = Json.Load<Dictionary<int, long>>(CachePath) ?? [];

    /// <summary>Octets demandés par ce jeu Steam, ou null si la page ne le dit pas (ou ne répond pas).</summary>
    public async Task<long?> GetAsync(int appId)
    {
        if (_bytes.TryGetValue(appId, out var known)) return known > 0 ? known : null;
        try
        {
            var bytes = await store.RequiredStorageAsync(appId, CancellationToken.None);
            // Pas de réponse du magasin : on réessaiera à la prochaine ouverture ; « rien sur la page » est retenu.
            if (bytes is null) return null;
            _bytes[appId] = bytes.Value;
            Json.Save(CachePath, _bytes);
            return bytes > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Taille du jeu Steam {appId}", ex);
            return null;
        }
    }
}
