using System.Text.Json.Serialization;
using MaRogAlly.Core;

namespace MaRogAlly.Models;

/// <summary>Un jeu trouvé par un scanner (installé) ou dans la bibliothèque d'un compte (possédé).</summary>
public sealed class Game : ObservableObject
{
    public string Id => $"{Platform}:{PlatformId}";

    public Platform Platform { get; set; }
    public string PlatformId { get; set; } = "";
    public string Title { get; set; } = "";

    public bool IsInstalled { get; set; }
    public string? InstallDir { get; set; }

    /// <summary>URI de lancement (steam://, uplay://…) ; sinon on lance <see cref="LaunchExe"/>.</summary>
    public string? LaunchUri { get; set; }
    public string? LaunchExe { get; set; }
    public string? LaunchArgs { get; set; }

    /// <summary>URI d'installation pour un jeu possédé mais non installé.</summary>
    public string? InstallUri { get; set; }

    public int? SteamAppId { get; set; }
    public int? PlaytimeMinutes { get; set; }
    /// <summary>Dernière partie selon la plateforme (Steam, GOG Galaxy) — y compris lancée hors de l'application.</summary>
    public DateTime? PlatformLastPlayed { get; set; }

    /// <summary>Visuels fournis directement par la plateforme (ex. cache Epic), prioritaires sur les recherches.</summary>
    public string? CoverUrl { get; set; }
    public string? HeroUrl { get; set; }
    public string? LogoUrl { get; set; }

    public DateTime FirstSeen { get; set; } = DateTime.Now;

    /// <summary>Clé partagée entre plateformes pour le cache des métadonnées.</summary>
    [JsonIgnore]
    public string MetadataKey => SteamAppId is int id ? $"steam-{id}" : $"name-{Text.Normalize(Title).Replace(' ', '-')}";

    [JsonIgnore] public PlatformInfo PlatformInfo => PlatformInfo.Of(Platform);
    /// <summary>Tags Steam (noms anglais), du plus voté au moins voté ; vide si le jeu n'est pas relié à Steam.</summary>
    [JsonIgnore] public IReadOnlyList<string> Tags { get; set; } = [];
    [JsonIgnore] public string Initial => Text.Initial(DisplayTitle);

    // ---- Données enrichies (métadonnées, préférences) : non sérialisées ici ----

    private GameMetadata? _meta;
    [JsonIgnore]
    public GameMetadata? Meta
    {
        get => _meta;
        set
        {
            if (!Set(ref _meta, value)) return;
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(RatingText));
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(GenresText));
            OnPropertyChanged(nameof(ReleaseYear));
            OnPropertyChanged(nameof(Studio));
        }
    }

    private string? _coverPath, _heroPath, _logoPath;
    [JsonIgnore] public string? CoverPath { get => _coverPath; set => Set(ref _coverPath, value); }
    [JsonIgnore] public string? HeroPath { get => _heroPath; set => Set(ref _heroPath, value); }
    [JsonIgnore] public string? LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }


    private DateTime? _lastPlayed;
    [JsonIgnore]
    public DateTime? LastPlayed
    {
        get => _lastPlayed;
        set { if (Set(ref _lastPlayed, value)) OnPropertyChanged(nameof(LastPlayedText)); }
    }

    private int _launchCount;
    [JsonIgnore] public int LaunchCount { get => _launchCount; set => Set(ref _launchCount, value); }

    private bool _isSelected;
    [JsonIgnore] public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isHidden;
    [JsonIgnore] public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Meta?.Title) ? Text.CleanTitle(Title) : Meta!.Title!;
    [JsonIgnore] public string Description => string.IsNullOrWhiteSpace(Meta?.Description) ? "Aucune description disponible pour le moment." : Meta!.Description!;
    [JsonIgnore] public double? Rating => Meta?.Rating;
    [JsonIgnore] public string RatingText => Meta?.Rating is double r ? $"{r:0}" : "–";
    [JsonIgnore] public string GenresText => Meta?.Genres is { Count: > 0 } g ? string.Join(" · ", g.Take(3)) : "";
    [JsonIgnore] public int? ReleaseYear => Meta?.ReleaseDate?.Year;
    [JsonIgnore] public string Studio => Meta?.Developer ?? Meta?.Publisher ?? "";

    /// <summary>Dernière partie connue : lancée depuis l'application ou directement depuis la plateforme.</summary>
    [JsonIgnore]
    public DateTime? LastPlayedAny => LastPlayed is { } a && PlatformLastPlayed is { } b ? (a > b ? a : b) : LastPlayed ?? PlatformLastPlayed;

    [JsonIgnore]
    public string LastPlayedText => LastPlayedAny switch
    {
        null => "Jamais joué",
        var d when d.Value.Date == DateTime.Today => "Joué aujourd'hui",
        var d when d.Value.Date == DateTime.Today.AddDays(-1) => "Joué hier",
        var d => $"Joué le {d.Value:dd/MM/yyyy}",
    };

    [JsonIgnore]
    public string PlaytimeText => PlaytimeMinutes is int m and > 0 ? $"{FormatDuration(m)} de jeu" : "";

    /// <summary>« 42 min », « 7 h », « 12 h 30 ».</summary>
    public static string FormatDuration(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 || minutes >= 600 => $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60:00}",
    };

    public void CopyInstallStateFrom(Game other)
    {
        IsInstalled = other.IsInstalled;
        InstallDir = other.InstallDir;
        LaunchUri = other.LaunchUri;
        LaunchExe = other.LaunchExe;
        LaunchArgs = other.LaunchArgs;
    }
}
