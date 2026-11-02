namespace MaRogAlly.Models;

/// <summary>Fiche d'un jeu récupérée en ligne (IGDB, SteamGridDB, magasin Steam). Mise en cache sur disque.</summary>
public sealed class GameMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>Note sur 100.</summary>
    public double? Rating { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTime? ReleaseDate { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public int? SteamAppId { get; set; }

    /// <summary>URL candidates, de la meilleure à la moins bonne (la première qui répond est gardée).</summary>
    public List<string> Covers { get; set; } = [];
    public List<string> Heroes { get; set; } = [];
    public List<string> Logos { get; set; } = [];
    public List<string> Screenshots { get; set; } = [];

    public List<string> Sources { get; set; } = [];
    /// <summary>Services déjà interrogés (trouvé ou non) : permet de compléter quand une clé est ajoutée.</summary>
    public List<string> Attempted { get; set; } = [];
    public DateTime FetchedAt { get; set; } = DateTime.Now;

    /// <summary>Vrai si rien n'a été trouvé : on ne retente qu'après quelques jours.</summary>
    public bool NotFound { get; set; }
}

/// <summary>Préférences de l'utilisateur pour un jeu (masqué, dernier lancement…).</summary>
public sealed class GameUserData
{
    public bool Hidden { get; set; }
    public DateTime? LastPlayed { get; set; }
    public int LaunchCount { get; set; }
}
