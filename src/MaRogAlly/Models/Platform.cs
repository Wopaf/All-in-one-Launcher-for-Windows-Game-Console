using System.Diagnostics;

namespace MaRogAlly.Models;

public enum Platform
{
    Steam,
    Epic,
    Gog,
    Ea,
    Ubisoft,
    BattleNet,
    Xbox,
    Amazon,
    Riot,
    Itch,
    Rockstar,
    Ankama,
    Minecraft,
    Rsi,
    Manual,
}

/// <summary>Description d'une plateforme : nom affiché, couleur, accroche, lanceur et page de téléchargement.</summary>
public sealed record PlatformInfo(
    Platform Id,
    string Name,
    string Short,
    string Color,
    string Tagline,
    string DownloadUrl,
    string? LauncherUri)
{
    public static readonly IReadOnlyList<PlatformInfo> All =
    [
        new(Platform.Steam,     "Steam",             "STEAM",  "#1B9FFF", "La boutique PC de Valve",                 "https://store.steampowered.com/about/", "steam://open/games"),
        new(Platform.Epic,      "Epic Games",        "EPIC",   "#E6E6E6", "Un jeu offert chaque semaine",            "https://store.epicgames.com/download", "com.epicgames.launcher://store"),
        new(Platform.Gog,       "GOG Galaxy",        "GOG",    "#B565FF", "Des jeux sans DRM, pour toujours",        "https://www.gog.com/galaxy", "goggalaxy://openGalaxyClient"),
        new(Platform.Ea,        "EA app",            "EA",     "#FF4747", "Les jeux Electronic Arts et EA Play",     "https://www.ea.com/ea-app", "origin2://library/open"),
        new(Platform.Ubisoft,   "Ubisoft Connect",   "UBI",    "#2D8CFF", "Les univers Ubisoft et Ubisoft+",         "https://ubisoftconnect.com/", "uplay://open"),
        new(Platform.BattleNet, "Battle.net",        "BNET",   "#00AEFF", "Blizzard et Call of Duty",                "https://download.battle.net/", "battlenet://"),
        new(Platform.Xbox,      "Xbox",              "XBOX",   "#3FCF4F", "Microsoft Store et PC Game Pass",         "https://www.xbox.com/apps/xbox-app-for-pc", "msxbox://"),
        new(Platform.Amazon,    "Amazon Games",      "AMAZON", "#FF9F1C", "Les jeux offerts avec Prime Gaming",      "https://gaming.amazon.com/home", "amazon-games://"),
        new(Platform.Riot,      "Riot Games",        "RIOT",   "#FF4655", "League of Legends, VALORANT…",            "https://www.riotgames.com/", null),
        new(Platform.Itch,      "itch.io",           "ITCH",   "#FA5C5C", "La scène indépendante",                   "https://itch.io/app", "itch://library"),
        new(Platform.Rockstar,  "Rockstar Games",    "R*",     "#FCAF17", "GTA, Red Dead Redemption",                "https://socialclub.rockstargames.com/rockstar-games-launcher", null),
        new(Platform.Ankama,    "Ankama Games",      "ANKAMA", "#F28C28", "Dofus, Wakfu, Waven…",                    "https://www.ankama.com/fr/launcher", "zaap://"),
        new(Platform.Minecraft, "Minecraft Launcher","MC",     "#62B47A", "Le lanceur de Minecraft",                 "https://www.minecraft.net/download", null),
        new(Platform.Rsi,       "RSI Launcher",      "RSI",    "#6FB8E6", "Star Citizen et Squadron 42",             "https://robertsspaceindustries.com/download", null),
        new(Platform.Manual,    "Ajoutés à la main", "PERSO",  "#9AA4B2", "Émulateurs et jeux sans lanceur",         "", null),
    ];

    public static PlatformInfo Of(Platform p) => All[(int)p];

    /// <summary>Processus du lanceur, pour savoir s'il est ouvert.</summary>
    private static readonly Dictionary<Platform, string[]> LauncherProcesses = new()
    {
        [Platform.Steam] = ["steam"],
        [Platform.Epic] = ["EpicGamesLauncher"],
        [Platform.Gog] = ["GalaxyClient"],
        [Platform.Ea] = ["EADesktop", "Origin"],
        [Platform.Ubisoft] = ["upc", "UbisoftConnect"],
        [Platform.BattleNet] = ["Battle.net"],
        [Platform.Xbox] = ["XboxPcApp"],
        [Platform.Amazon] = ["Amazon Games UI", "Amazon Games"],
        [Platform.Riot] = ["RiotClientServices", "Riot Client"],
        [Platform.Itch] = ["itch"],
        [Platform.Rockstar] = ["RockstarService", "LauncherPatcher"],
        [Platform.Ankama] = ["Ankama Launcher"],
        [Platform.Minecraft] = ["MinecraftLauncher", "Minecraft"],
        [Platform.Rsi] = ["RSI Launcher"],
    };

    public IReadOnlyList<string> LauncherProcessNames => LauncherProcesses.GetValueOrDefault(Id, []);

    /// <summary>Le lanceur de la plateforme est-il ouvert en ce moment ?</summary>
    public bool IsLauncherRunning => LauncherProcesses.TryGetValue(Id, out var names)
        && names.Any(n => { var p = Process.GetProcessesByName(n); foreach (var x in p) x.Dispose(); return p.Length > 0; });

    /// <summary>Tracé vectoriel du logo (24×24), utilisable directement comme Path.Data.</summary>
    public string Logo => PlatformLogos.Get(Id);
}
