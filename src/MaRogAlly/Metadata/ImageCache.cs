using System.Net.Http;
using MaRogAlly.Core;

namespace MaRogAlly.Metadata;

/// <summary>Télécharge une seule fois chaque visuel dans %LOCALAPPDATA%\MaRogAlly\images.</summary>
public sealed class ImageCache(HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(8, 8);

    /// <summary>Adresses déjà essayées sans succès pendant la session (ex. logo Steam inexistant) : on ne les redemande pas.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Failed = new();

    public static string PathFor(string key, string kind) => Path.Combine(AppPaths.ImagesDir, $"{key}_{kind}.img");

    public static string? Existing(string key, string kind)
    {
        var p = PathFor(key, kind);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Essaie les URL dans l'ordre et garde la première image valide.</summary>
    public async Task<string?> GetAsync(string key, string kind, IEnumerable<string?> candidates, CancellationToken ct)
    {
        var target = PathFor(key, kind);
        if (File.Exists(target)) return target;

        foreach (var url in candidates.OfType<string>().Distinct().Where(u => !Failed.ContainsKey(u)))
        {
            await _gate.WaitAsync(ct);
            try
            {
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var bytes = res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true
                    ? await res.Content.ReadAsByteArrayAsync(ct)
                    : [];
                if (bytes.Length < 1024) // erreur, page HTML ou image vide de remplacement
                {
                    Failed[url] = 0;
                    continue;
                }
                var tmp = target + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, target, true);
                return target;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Image non téléchargée ({url})", ex);
            }
            finally
            {
                _gate.Release();
            }
        }
        return null;
    }
}
