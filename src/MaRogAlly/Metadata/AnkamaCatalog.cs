using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Metadata;

/// <summary>
/// Fiches des jeux Ankama : le lanceur ne donne ni genre ni description, et ils sont souvent absents du magasin Steam.
/// Ces fiches passent avant les sources en ligne, qui complètent ce qui manque (visuels notamment).
/// </summary>
public static class AnkamaCatalog
{
    private sealed record Sheet(string[] Genres, string Description, int? Year = null);

    private const string DofusText = "MMORPG tactique au tour par tour dans l'univers du Krosmoz : incarne un aventurier, explore le Monde des Douze, affronte ses monstres et pars à la recherche des Dofus, les œufs de dragon.";

    private static readonly Dictionary<string, Sheet> Sheets = new()
    {
        ["dofus"] = new(["MMORPG", "Stratégie", "Tour par tour"], DofusText, 2004),
        ["dofus 2"] = new(["MMORPG", "Stratégie", "Tour par tour"], DofusText, 2004),
        ["dofus retro"] = new(["MMORPG", "Stratégie", "Tour par tour"], "La version d'origine de Dofus, remise en service par Ankama : le même MMORPG tactique au tour par tour, avec les graphismes et les règles de ses débuts.", 2019),
        ["wakfu"] = new(["MMORPG", "Stratégie", "Tour par tour"], "MMORPG tactique au tour par tour qui se déroule dans le Krosmoz, mille ans après Dofus : un monde vivant à reconstruire, dont les joueurs influencent l'écosystème.", 2012),
        ["waven"] = new(["RPG", "Stratégie", "Tour par tour"], "RPG tactique au tour par tour dans l'univers du Krosmoz, où le Monde des Douze a été englouti par les eaux."),
        ["krosmaga"] = new(["Cartes", "Stratégie"], "Jeu de cartes et de stratégie en ligne dans l'univers du Krosmoz."),
        ["fly'n"] = new(["Plateforme", "Aventure"], "Jeu de plateforme et d'aventure développé par Ankama."),
    };

    /// <summary>Complète la fiche d'un jeu connu ; ne remplace rien de ce qu'elle contient déjà.</summary>
    public static GameMetadata? Complete(string title, GameMetadata? meta)
    {
        if (!Sheets.TryGetValue(Text.Normalize(title), out var sheet) && !Sheets.TryGetValue(title.ToLowerInvariant(), out sheet)) return meta;
        meta = meta is null or { NotFound: true } ? new GameMetadata() : meta;
        meta.Description ??= sheet.Description;
        if (meta.Genres.Count == 0) meta.Genres = [.. sheet.Genres];
        meta.Developer ??= "Ankama";
        meta.Publisher ??= "Ankama";
        if (meta.ReleaseDate is null && sheet.Year is int year) meta.ReleaseDate = new DateTime(year, 1, 1);
        if (!meta.Sources.Contains("Ankama")) meta.Sources.Add("Ankama");
        return meta;
    }
}
