using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>Version publiée sur GitHub, plus récente que celle qui tourne.</summary>
public sealed record AppRelease(Version Version, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Mise à jour de l'application depuis les versions publiées sur GitHub : l'exe est téléchargé à côté de l'exe actuel, vérifié,
/// puis les deux échangent leur place (Windows autorise à renommer un exe en cours d'exécution) avant un redémarrage.
/// </summary>
public sealed class AppUpdater(HttpClient http)
{
    private const string LatestRelease = "https://api.github.com/repos/Wopaf/All-in-one-Launcher-for-Windows-Game-Console/releases/latest";
    private const string AssetName = "MaRogAlly.exe";
    public const string UpdatedArg = "--updated";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string CurrentText => Current.ToString(3);

    private static string Exe => Environment.ProcessPath ?? "";

    /// <summary>Vrai pour l'exe publié : une version de développement ne se remplace pas elle-même.</summary>
    public static bool CanApply
    {
#if DEBUG
        get => false;
#else
        get => Path.GetFileName(Exe).Equals(AssetName, StringComparison.OrdinalIgnoreCase);
#endif
    }

    /// <summary>La dernière version publiée si elle est plus récente, sinon null (ou si GitHub ne répond pas).</summary>
    public async Task<AppRelease?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                return new AppRelease(latest, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                    digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null);
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // délai dépassé
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Error("Vérification de la mise à jour", ex);
            return null;
        }
    }

    /// <summary>Télécharge et vérifie le nouvel exe (taille et empreinte) ; renvoie son chemin.</summary>
    public async Task<string> DownloadAsync(AppRelease release, Action<double> progress, CancellationToken ct)
    {
        var target = Exe + ".new";
        using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var sha = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;
                progress((double)total / release.Size);
            }
            if (total != release.Size) throw new InvalidDataException($"Téléchargement incomplet ({total} sur {release.Size} octets)");
        }
        sha.TransformFinalBlock([], 0, 0);
        if (release.Sha256 is { } expected && !Convert.ToHexString(sha.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException("Le fichier téléchargé est corrompu (empreinte différente)");
        }
        return target;
    }

    /// <summary>Remplace l'exe et relance l'application avec les mêmes options. En cas d'échec, l'ancien exe est remis en place.</summary>
    public static void ApplyAndRestart(string newExe)
    {
        var exe = Exe;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => a != UpdatedArg)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(UpdatedArg);
        Process.Start(start);
        Application.Current.Shutdown();
    }

    /// <summary>Après une mise à jour : retire l'ancien exe, une fois que l'ancienne instance l'a libéré.</summary>
    public static async Task CleanUpAsync()
    {
        foreach (var leftover in new[] { Exe + ".old", Exe + ".new" })
            for (var attempt = 0; attempt < 10 && File.Exists(leftover); attempt++)
            {
                try { File.Delete(leftover); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
            }
    }
}
