using System.IO;
using System.IO.Enumeration;
using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Services;

/// <summary>
/// Place prise par chaque jeu installé, pour le tri par poids : la dernière mesure connue s'affiche tout de suite,
/// et chaque dossier est remesuré une fois par session. S'arrête au lancement d'un jeu.
/// </summary>
public sealed class GameSizes
{
    private static readonly EnumerationOptions Options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static string CachePath => Path.Combine(AppPaths.Root, "game-sizes.json");

    private readonly Dictionary<string, long> _bytes = new(Json.Load<Dictionary<string, long>>(CachePath) ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _measured = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public bool IsMeasuring => _cts is not null;

    public void Cancel() => _cts?.Cancel();

    public void Apply(IEnumerable<Game> games)
    {
        foreach (var g in games)
            g.DiskBytes = g.IsInstalled && g.InstallDir is { } dir && _bytes.TryGetValue(StorageScanner.Norm(dir), out var bytes) ? bytes : null;
    }

    /// <summary>Mesure les dossiers pas encore vus dans cette session ; vrai si une taille a changé.</summary>
    public async Task<bool> MeasureAsync(IEnumerable<Game> games, Action<int, int> progress)
    {
        var dirs = games.Where(g => g.IsInstalled && g.InstallDir is not null)
            .Select(g => StorageScanner.Norm(g.InstallDir!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !_measured.Contains(d))
            .ToList();
        if (dirs.Count == 0) return false;

        var changed = false;
        using var cts = _cts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < dirs.Count; i++)
            {
                progress(i, dirs.Count);
                var dir = dirs[i];
                var bytes = await Task.Run(() => Measure(dir, cts.Token), cts.Token);
                _measured.Add(dir);
                changed |= !_bytes.TryGetValue(dir, out var old) || old != bytes;
                _bytes[dir] = bytes;
            }
        }
        catch (OperationCanceledException) { }
        finally { _cts = null; }

        if (changed) Json.Save(CachePath, _bytes);
        return changed;
    }

    private static long Measure(string dir, CancellationToken ct)
    {
        long total = 0;
        try
        {
            var files = new FileSystemEnumerable<long>(dir, (ref FileSystemEntry e) => e.Length, Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            foreach (var length in files)
            {
                ct.ThrowIfCancellationRequested();
                total += length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}
