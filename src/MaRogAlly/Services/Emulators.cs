using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaRogAlly.Core;
using MaRogAlly.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MaRogAlly.Services;

/// <summary>Un émulateur proposé dans l'onglet Émulateurs, téléchargé depuis les versions publiées sur GitHub par ses auteurs.</summary>
public sealed record EmulatorInfo(
    string Id,
    Platform Platform,
    string Name,
    string Pitch,
    string Repo,
    Regex Asset,
    string Exe,
    string[] Extensions,
    string ArgsFormat,
    string? PortableFile,
    string FirstRunNote)
{
    public PlatformInfo PlatformInfo => PlatformInfo.Of(Platform);

    /// <summary>Dossier de l'émulateur : %LOCALAPPDATA%\MaRogAlly\Emulators\{Id}.</summary>
    public string Dir => Path.Combine(Emulators.Root, Id);
    public string ExePath => Path.Combine(Dir, Exe);

    /// <summary>Dossier de jeux de la console, toujours scanné (créé à l'installation).</summary>
    public string RomDir => Path.Combine(Emulators.RomsRoot, PlatformInfo.Short);

    /// <summary>Arguments pour lancer un jeu (plein écran).</summary>
    public string ArgsFor(string rom) => ArgsFormat.Replace("{rom}", $"\"{rom}\"");
}

/// <summary>Version d'un émulateur installée par l'application.</summary>
public sealed record EmulatorInstall(string Version, DateTime InstalledAt);

/// <summary>Dernière version publiée d'un émulateur.</summary>
public sealed record EmulatorRelease(string Version, string DownloadUrl, string FileName, long Size, string? Sha256);

/// <summary>
/// Émulateurs proposés (un par console), leur installation, mise à jour et désinstallation.
/// Chaque émulateur est la version portable de ses auteurs, décompressée dans son propre dossier :
/// ses réglages et sauvegardes y restent, rien n'est installé ailleurs dans Windows.
/// </summary>
public static class Emulators
{
    public static string Root => Path.Combine(AppPaths.Root, "Emulators");
    public static string RomsRoot => Path.Combine(AppPaths.Root, "Roms");
    private const string MarkerFile = ".marogally.json";

    public static readonly IReadOnlyList<EmulatorInfo> All =
    [
        new("mesen", Platform.Nes, "Mesen",
            "Le plus fidèle des émulateurs NES : précis, léger, avec des filtres d'image soignés.",
            "SourMesen/Mesen2", new Regex(@"^Mesen_[\d.]+_Windows\.zip$", RegexOptions.IgnoreCase), "Mesen.exe",
            [".nes", ".fds", ".unf", ".unif", ".zip", ".7z"], "{rom} --fullscreen", null,
            "Au premier lancement, Mesen demande où ranger ses réglages : choisis le dossier de l'émulateur (portable)."),
        new("mgba", Platform.Gba, "mGBA",
            "La référence sur Game Boy Advance : précis, rapide, et la manette est reconnue d'emblée.",
            "mgba-emu/mgba", new Regex(@"^mGBA-[\d.]+-win64\.7z$", RegexOptions.IgnoreCase), "mGBA.exe",
            [".gba", ".zip", ".7z"], "-f {rom}", "portable.ini",
            "Les sauvegardes des jeux sont rangées à côté des fichiers des jeux."),
        new("rmg", Platform.N64, "Rosalie's Mupen GUI",
            "Mupen64Plus dans une interface simple : le meilleur choix actuel sur Nintendo 64.",
            "Rosalie241/RMG", new Regex(@"^RMG-Portable-Windows64-v[\d.]+\.zip$", RegexOptions.IgnoreCase), "RMG.exe",
            [".z64", ".n64", ".v64", ".zip", ".7z"], "-f -n -q {rom}", null,
            "Règle la manette une fois dans RMG (Réglages › Manette) avant de jouer."),
    ];

    public static EmulatorInfo? For(Platform platform) => All.FirstOrDefault(e => e.Platform == platform);

    public static EmulatorInstall? Installed(EmulatorInfo e)
    {
        if (!File.Exists(e.ExePath)) return null;
        return Json.Load<EmulatorInstall>(Path.Combine(e.Dir, MarkerFile)) ?? new EmulatorInstall("?", File.GetLastWriteTime(e.ExePath));
    }

    public static bool IsInstalled(EmulatorInfo e) => File.Exists(e.ExePath);

    /// <summary>Dossiers de jeux de la console : celui de l'application, puis ceux ajoutés (s'ils existent encore).</summary>
    public static List<string> RomFolders(EmulatorInfo e, AppSettings settings)
    {
        var folders = new List<string> { e.RomDir };
        if (settings.RomFolders.TryGetValue(e.Platform, out var extra))
            folders.AddRange(extra.Where(f => !folders.Contains(f, StringComparer.OrdinalIgnoreCase)));
        return folders;
    }

    /// <summary>L'émulateur tourne (fichiers verrouillés : ni mise à jour ni désinstallation).</summary>
    public static bool IsRunning(EmulatorInfo e)
    {
        var name = Path.GetFileNameWithoutExtension(e.Exe);
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
                try
                {
                    if (string.Equals(p.MainModule?.FileName, e.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
        return false;
    }

    /// <summary>Dernière version publiée sur GitHub (null si GitHub ne répond pas ou si le fichier attendu manque).</summary>
    public static async Task<EmulatorRelease?> LatestAsync(HttpClient http, EmulatorInfo e, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{e.Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"Émulateur {e.Name} : GitHub a répondu {(int)response.StatusCode}");
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var version = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!e.Asset.IsMatch(name)) continue;
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                return new EmulatorRelease(version, asset.GetProperty("browser_download_url").GetString()!, name,
                    asset.GetProperty("size").GetInt64(),
                    digest is not null && digest.StartsWith("sha256:") ? digest["sha256:".Length..] : null);
            }
            Log.Info($"Émulateur {e.Name} : aucun fichier Windows dans la version {version}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Recherche de la version de {e.Name}", ex);
        }
        return null;
    }

    /// <summary>Une version plus récente que celle installée (comparaison des numéros, sinon du texte).</summary>
    public static bool IsNewer(EmulatorRelease release, EmulatorInstall installed)
    {
        if (installed.Version == "?") return true;
        if (Version.TryParse(release.Version, out var a) && Version.TryParse(installed.Version, out var b)) return a > b;
        return !string.Equals(release.Version, installed.Version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Télécharge, vérifie et décompresse la version dans le dossier de l'émulateur. Une mise à jour remplace les fichiers de
    /// l'émulateur et garde les autres (réglages, sauvegardes). progress : de 0 à 1 pendant le téléchargement.
    /// </summary>
    public static async Task InstallAsync(HttpClient http, EmulatorInfo e, EmulatorRelease release, IProgress<double> progress, CancellationToken ct)
    {
        var work = Path.Combine(Root, ".work", e.Id);
        var archive = Path.Combine(work, release.FileName);
        var staging = Path.Combine(work, "files");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            Log.Info($"Émulateur {e.Name} : téléchargement de {release.Version} ({release.FileName})");
            using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(archive);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress.Report(Math.Min(1, (double)done / total));
                }
            }

            // Fichier abîmé ou modifié en route : rien n'est installé.
            if (release.Sha256 is { } expected)
            {
                await using var stream = File.OpenRead(archive);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Le fichier téléchargé ne correspond pas à celui publié (somme de contrôle).");
            }

            await Task.Run(() => Extract(archive, staging), ct);
            // Archive avec un seul dossier à la racine (mGBA-0.10.5-win64\…) : son contenu est l'émulateur.
            var content = staging;
            while (Directory.GetFiles(content).Length == 0 && Directory.GetDirectories(content) is [var only]) content = only;
            if (!File.Exists(Path.Combine(content, e.Exe)))
                throw new InvalidDataException($"{e.Exe} est introuvable dans le fichier téléchargé.");

            await Task.Run(() => CopyOver(content, e.Dir), ct);
            if (e.PortableFile is { } portable && !File.Exists(Path.Combine(e.Dir, portable)))
                File.WriteAllText(Path.Combine(e.Dir, portable), "");
            Directory.CreateDirectory(e.RomDir);
            Json.Save(Path.Combine(e.Dir, MarkerFile), new EmulatorInstall(release.Version, DateTime.Now));
            Log.Info($"Émulateur {e.Name} : {release.Version} installé");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>Supprime le dossier de l'émulateur (réglages et sauvegardes rangées dedans compris). Les jeux restent.</summary>
    public static void Uninstall(EmulatorInfo e)
    {
        if (Directory.Exists(e.Dir)) Directory.Delete(e.Dir, true);
        Log.Info($"Émulateur {e.Name} : désinstallé");
    }

    private static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }
        using var file = ArchiveFactory.Open(archive);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        // Lecture d'un bout à l'autre : un .7z « solide » se décompresse en une passe, pas fichier par fichier.
        using var reader = file.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            // Chemin qui sortirait du dossier (« ..\ ») : ignoré.
            var target = Path.GetFullPath(Path.Combine(destination, entry.Key ?? ""));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Fichier vide : le .7z ne lui donne pas de contenu à lire.
            if (entry.Size == 0) File.WriteAllBytes(target, []);
            else reader.WriteEntryToFile(target, new ExtractionOptions { Overwrite = true });
        }
    }

    private static void CopyOver(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
