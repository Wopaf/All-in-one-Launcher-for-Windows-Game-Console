using MaRogAlly.Core;

namespace MaRogAlly.Models;

/// <summary>Réglages enregistrés dans %LOCALAPPDATA%\MaRogAlly\settings.json.</summary>
public sealed class AppSettings
{
    // IGDB (l'équivalent de TMDB pour les jeux) : clés gratuites sur https://dev.twitch.tv/console
    public string IgdbClientId { get; set; } = "";
    public string IgdbClientSecret { get; set; } = "";

    // SteamGridDB (logos, fonds, jaquettes) : clé gratuite sur https://www.steamgriddb.com/profile/preferences/api
    public string SteamGridDbKey { get; set; } = "";

    // Steam : pour lister les jeux possédés non installés. Clé sur https://steamcommunity.com/dev/apikey
    public string SteamApiKey { get; set; } = "";
    /// <summary>SteamID64 ; détecté automatiquement s'il est vide.</summary>
    public string SteamId { get; set; } = "";

    /// <summary>Mode de la manette choisi dans le centre de contrôle (Auto, Gamepad, Desktop) ; vide : on n'y touche pas.</summary>
    public string? ControlMode { get; set; }

    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeOnLaunch { get; set; } = false;
    /// <summary>Masque automatiquement la barre des tâches de Windows tant que l'application tourne.</summary>
    public bool HideWindowsTaskbar { get; set; } = true;
    /// <summary>Réglage de Windows à remettre en partant (conservé si l'application s'arrête brutalement).</summary>
    public bool? TaskbarAutoHideBefore { get; set; }
    /// <summary>Le bouton B de la manette réduit l'application au premier plan (jamais un jeu).</summary>
    public bool MinimizeWithB { get; set; } = true;
    /// <summary>Lanceurs ouverts avec l'application, s'ils sont installés.</summary>
    public HashSet<Platform> StartupLaunchers { get; set; } = [Platform.Steam, Platform.Epic];
    /// <summary>Cherche toute seule une nouvelle version sur GitHub (l'installation reste toujours à valider).</summary>
    public bool CheckAppUpdates { get; set; } = true;
    /// <summary>Sons de navigation (déplacement, validation, ouverture).</summary>
    public bool UiSounds { get; set; } = true;

    /// <summary>Échelle de l'interface (0 = automatique : réduite sur petit écran comme la ROG Ally).</summary>
    public double UiScale { get; set; }

    /// <summary>Thème de l'application (identifiant de <see cref="UI.AppTheme"/>).</summary>
    public string Theme { get; set; } = UI.AppTheme.Default;

    /// <summary>Couleur d'accent de l'interface (« #RRGGBB »).</summary>
    public string AccentColor { get; set; } = "#FF2E4D";

    /// <summary>Catégories affichées en boutons au-dessus de la Bibliothèque.</summary>
    public List<string> LibraryCategories { get; set; } = [.. GameCategories.Defaults];

    /// <summary>Applications favorites du menu Windows maison (identifiants, dans l'ordre d'ajout).</summary>
    public List<string> FavoriteApps { get; set; } = [];

    /// <summary>Applications lancées depuis le menu Windows maison (identifiant, date).</summary>
    public Dictionary<string, DateTime> RecentApps { get; set; } = [];

    /// <summary>Dernier tri choisi par onglet.</summary>
    public Dictionary<string, SortMode> Sorts { get; set; } = [];

    /// <summary>Jeux ajoutés à la main (exécutables).</summary>
    public List<ManualGame> ManualGames { get; set; } = [];

    public static AppSettings Load() => Json.Load<AppSettings>(AppPaths.Settings) ?? new AppSettings();
    public void Save() => Json.Save(AppPaths.Settings, this);

    public bool HasIgdb => !string.IsNullOrWhiteSpace(IgdbClientId) && !string.IsNullOrWhiteSpace(IgdbClientSecret);
    public bool HasSteamGridDb => !string.IsNullOrWhiteSpace(SteamGridDbKey);
}

public sealed class ManualGame
{
    public string Title { get; set; } = "";
    public string Exe { get; set; } = "";
    public string? Args { get; set; }
}
