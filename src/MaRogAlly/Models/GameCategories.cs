using MaRogAlly.Core;

namespace MaRogAlly.Models;

/// <summary>Catégorie proposée pour les boutons de la Bibliothèque : plusieurs tags Steam réunis sous un nom simple.</summary>
public sealed record GameCategory(string Key, string Label, Func<IReadOnlyList<string>, bool> Matches);

/// <summary>
/// Catégories des jeux, d'après leurs tags Steam (classés du plus voté au moins voté).
/// Seuls les premiers tags comptent : les suivants sont trop approximatifs (Baldur's Gate 3 n'est pas un jeu de stratégie).
/// </summary>
public static class GameCategories
{
    /// <summary>Boutons affichés par défaut.</summary>
    public static readonly string[] Defaults = ["fps", "rogue", "narrative", "online", "indie", "survival", "cozy", "strategy"];

    public const int MaxButtons = 8;
    private const string TagPrefix = "tag:";
    private const int RawTagDepth = 10;

    /// <summary>Nom affiché d'un tag Steam (français si connu).</summary>
    public static Func<string, string> TagLabel { get; set; } = name => name;

    private static bool Top(IReadOnlyList<string> tags, int depth, params string[] names)
        => tags.Take(depth).Any(names.Contains);

    public static readonly IReadOnlyList<GameCategory> Curated =
    [
        new("fps", "FPS", t => Top(t, 5, "FPS")),
        new("rogue", "Rogue", t => Top(t, 8, "Roguelike", "Roguelite", "Action Roguelike", "Traditional Roguelike", "Roguelike Deckbuilder")),
        new("narrative", "Narratif", t => Top(t, 6, "Story Rich", "Visual Novel", "Interactive Fiction", "Choose Your Own Adventure", "Choices Matter", "Walking Simulator")),
        new("online", "En ligne", t => Top(t, 10, "Massively Multiplayer", "Online Co-Op", "PvP", "MOBA", "Battle Royale")),
        new("indie", "Indé", t => Top(t, 20, "Indie")),
        // « Survival » seul mélange PUBG et Among Us : il faut aussi du craft ou de la construction.
        new("survival", "Survie", t => Top(t, 20, "Open World Survival Craft") || (Top(t, 10, "Survival") && Top(t, 20, "Crafting", "Base Building"))),
        new("cozy", "Cosy", t => Top(t, 10, "Cozy", "Wholesome", "Farming Sim", "Life Sim")),
        new("strategy", "Stratégie", t => Top(t, 5, "Strategy", "RTS", "Turn-Based Strategy", "Grand Strategy", "4X", "Tower Defense", "Turn-Based Tactics")),
        new("souls", "Souls", t => Top(t, 10, "Souls-like")),
        new("sport", "Sport & Course", t => Top(t, 8, "Sports", "Racing", "Automobile Sim", "Football (Soccer)", "Basketball", "Golf", "Tennis", "Skateboarding", "Snowboarding", "Baseball", "Football (American)", "Hockey")),
        new("fighting", "Combat", t => Top(t, 8, "Fighting", "2D Fighter", "3D Fighter")),
        new("building", "Construction", t => Top(t, 8, "Base Building", "City Builder", "Automation", "Colony Sim", "Building")),
        new("deck", "Deckbuilding", t => Top(t, 8, "Deckbuilding", "Card Battler", "Roguelike Deckbuilder")),
        new("builds", "Builds & loot", t => Top(t, 5, "Action RPG", "Hack and Slash", "Looter Shooter", "Loot")),
        new("rpg", "RPG", t => Top(t, 6, "RPG", "JRPG", "CRPG", "Action RPG", "Party-Based RPG", "Turn-Based RPG")),
        new("action", "Action", t => Top(t, 5, "Action")),
        new("adventure", "Aventure", t => Top(t, 5, "Adventure")),
        new("horror", "Horreur", t => Top(t, 8, "Horror", "Survival Horror", "Psychological Horror")),
        new("platformer", "Plateforme", t => Top(t, 8, "Platformer", "2D Platformer", "3D Platformer", "Precision Platformer")),
        new("metroidvania", "Metroidvania", t => Top(t, 8, "Metroidvania")),
        new("coop", "Coop", t => Top(t, 10, "Co-op", "Online Co-Op", "Local Co-Op")),
        new("openworld", "Monde ouvert", t => Top(t, 8, "Open World")),
        new("puzzle", "Puzzle", t => Top(t, 8, "Puzzle")),
        new("simulation", "Simulation", t => Top(t, 5, "Simulation")),
    ];

    /// <summary>Jeux absents de Steam, donc sans tags : classés à la main (titre normalisé).</summary>
    private static readonly Dictionary<string, string[]> WithoutSteam = new()
    {
        ["fortnite"] = ["online", "fps"],
        ["league of legends"] = ["online", "strategy"],
        ["genshin impact"] = ["online", "rpg", "openworld"],
        ["hearthstone"] = ["online", "strategy", "deck"],
        ["world of warcraft"] = ["online", "rpg", "openworld"],
        ["starcraft"] = ["strategy"],
        ["starcraft ii"] = ["online", "strategy"],
        ["diablo iii"] = ["builds", "rpg", "coop", "action"],
        ["heroes of the storm"] = ["online", "strategy"],
        ["dofus"] = ["online", "rpg", "strategy"],
        ["dofus retro"] = ["online", "rpg", "strategy"],
        ["minecraft for windows"] = ["survival", "building", "openworld"],
    };

    /// <summary>Logiciels vendus sur Steam (ex. Lossless Scaling, tagué FPS) : hors catégories de jeux.</summary>
    private static bool IsSoftware(IReadOnlyList<string> tags) => Top(tags, 20, "Utilities", "Software");

    public static GameCategory? Find(string key) => Curated.FirstOrDefault(c => c.Key == key);
    public static string KeyOfTag(string englishName) => TagPrefix + englishName;

    public static string Label(string key)
        => key.StartsWith(TagPrefix, StringComparison.Ordinal) ? TagLabel(key[TagPrefix.Length..]) : Find(key)?.Label ?? key;

    public static bool Matches(Game game, string key)
    {
        if (key.StartsWith(TagPrefix, StringComparison.Ordinal))
            return Top(game.Tags, RawTagDepth, key[TagPrefix.Length..]);
        if (game.Tags.Count == 0)
            return WithoutSteam.TryGetValue(Text.Normalize(game.Title), out var keys) && keys.Contains(key);
        return !IsSoftware(game.Tags) && Find(key)?.Matches(game.Tags) == true;
    }

    /// <summary>Tags présents parmi ces jeux, du plus fréquent au moins fréquent (les premiers tags de chaque jeu seulement).</summary>
    public static List<(string Tag, int Count)> TagsOf(IEnumerable<Game> games)
        => games.SelectMany(g => g.Tags.Take(RawTagDepth)).GroupBy(t => t)
            .Select(x => (x.Key, x.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
}
