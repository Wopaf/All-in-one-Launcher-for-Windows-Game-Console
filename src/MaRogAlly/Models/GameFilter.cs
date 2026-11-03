using MaRogAlly.Core;

namespace MaRogAlly.Models;

public enum SortMode { NameAsc, NameDesc, LastPlayed, MostPlayed, Rating, Release, RecentlyAdded, Platform, Size }

public enum InstallFilter { All, Installed, NotInstalled }

/// <summary>Filtres et tri d'un onglet de jeux.</summary>
public sealed class GameFilter : ObservableObject
{
    public static readonly IReadOnlyList<(SortMode Mode, string Label)> SortModes =
    [
        (SortMode.NameAsc, "Nom (A → Z)"),
        (SortMode.NameDesc, "Nom (Z → A)"),
        (SortMode.LastPlayed, "Joués récemment"),
        (SortMode.MostPlayed, "Les plus joués"),
        (SortMode.Rating, "Les mieux notés"),
        (SortMode.Release, "Date de sortie"),
        (SortMode.RecentlyAdded, "Ajoutés récemment"),
        (SortMode.Size, "Les plus lourds"),
        (SortMode.Platform, "Plateforme"),
    ];

    public HashSet<Platform> Platforms { get; } = [];
    public InstallFilter Install { get; set; }
    public bool ShowHidden { get; set; }
    public string? Genre { get; set; }
    /// <summary>Bouton de catégorie actif (voir <see cref="GameCategories"/>).</summary>
    public string? Category { get; set; }
    public string Search { get; set; } = "";
    public SortMode Sort { get; set; } = SortMode.NameAsc;

    public bool IsActive => Platforms.Count > 0 || Install != InstallFilter.All || ShowHidden
                            || Genre is not null || Category is not null || !string.IsNullOrWhiteSpace(Search);

    public void Reset()
    {
        Platforms.Clear();
        Install = InstallFilter.All;
        ShowHidden = false;
        Genre = null;
        Category = null;
        Search = "";
    }

    public string SortLabel => SortModes.First(s => s.Mode == Sort).Label;

    /// <summary>Résumé lisible des filtres actifs (affiché au-dessus de la grille).</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Search)) parts.Add($"« {Search} »");
            if (Platforms.Count > 0) parts.Add(string.Join(", ", Platforms.Select(p => PlatformInfo.Of(p).Name)));
            if (Install == InstallFilter.Installed) parts.Add("Installés");
            if (Install == InstallFilter.NotInstalled) parts.Add("Non installés");
            if (Category is not null) parts.Add(GameCategories.Label(Category));
            if (Genre is not null) parts.Add(Genre);
            if (ShowHidden) parts.Add("Masqués inclus");
            return parts.Count == 0 ? "Tous les jeux" : string.Join(" · ", parts);
        }
    }

    public List<Game> Apply(IEnumerable<Game> games)
    {
        var search = Text.Normalize(Search);
        var q = games.Where(g =>
            (ShowHidden || !g.IsHidden)
            && (Platforms.Count == 0 || Platforms.Contains(g.Platform))
            && Install switch
            {
                InstallFilter.Installed => g.IsInstalled,
                InstallFilter.NotInstalled => !g.IsInstalled,
                _ => true,
            }
            && (Genre is null || (g.Meta?.Genres.Contains(Genre) ?? false))
            && (Category is null || GameCategories.Matches(g, Category))
            && (search.Length == 0 || Text.Normalize(g.DisplayTitle).Contains(search)));

        var byName = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true);
        IOrderedEnumerable<Game> sorted = Sort switch
        {
            SortMode.NameDesc => q.OrderByDescending(g => g.DisplayTitle, byName),
            SortMode.LastPlayed => q.OrderByDescending(g => g.LastPlayedAny ?? DateTime.MinValue).ThenByDescending(g => g.PlaytimeMinutes ?? 0),
            SortMode.MostPlayed => q.OrderByDescending(g => g.PlaytimeMinutes ?? 0).ThenByDescending(g => g.LaunchCount),
            SortMode.Rating => q.OrderByDescending(g => g.Rating ?? -1),
            SortMode.Release => q.OrderByDescending(g => g.Meta?.ReleaseDate ?? DateTime.MinValue),
            SortMode.RecentlyAdded => q.OrderByDescending(g => g.FirstSeen),
            SortMode.Platform => q.OrderBy(g => (int)g.Platform),
            SortMode.Size => q.OrderByDescending(g => g.DiskBytes ?? -1),
            _ => q.OrderBy(g => g.DisplayTitle, byName),
        };
        // Ordre alphabétique pour départager.
        return sorted.ThenBy(g => g.DisplayTitle, byName).ToList();
    }
}
