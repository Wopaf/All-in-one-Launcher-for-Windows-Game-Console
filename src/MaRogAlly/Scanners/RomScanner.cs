using System.Text.RegularExpressions;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Services;

namespace MaRogAlly.Scanners;

/// <summary>
/// Jeux d'une console émulée : les fichiers de jeux (ROM) de ses dossiers, lancés par l'émulateur installé depuis l'onglet
/// Émulateurs. Sans émulateur installé, rien n'est listé.
/// </summary>
public sealed partial class RomScanner(EmulatorInfo emulator) : GameScanner
{
    /// <summary>Profondeur des sous-dossiers parcourus (un dossier par série, par lettre…).</summary>
    private const int MaxDepth = 3;

    public override Platform Platform => emulator.Platform;

    public override bool IsClientInstalled() => Emulators.IsInstalled(emulator);

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
    {
        if (!Emulators.IsInstalled(emulator)) yield break;
        var seen = new HashSet<string>();
        foreach (var folder in Emulators.RomFolders(emulator, ctx.Settings))
            foreach (var rom in Files(folder, 0))
            {
                var title = TitleOf(rom);
                var id = Text.Normalize(title).Replace(' ', '-');
                // Même jeu en double (.nes et .zip) : le premier trouvé.
                if (id.Length == 0 || !seen.Add(id)) continue;
                yield return new Game
                {
                    Platform = emulator.Platform,
                    PlatformId = id,
                    Title = title,
                    IsInstalled = true,
                    LaunchExe = emulator.ExePath,
                    LaunchArgs = emulator.ArgsFor(rom),
                };
            }
    }

    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Game>>([]);

    private IEnumerable<string> Files(string folder, int depth)
    {
        if (!Directory.Exists(folder)) yield break;
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(folder);
            dirs = depth < MaxDepth ? Directory.GetDirectories(folder) : [];
        }
        catch { yield break; }
        foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase))
            if (emulator.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && (emulator.Accept?.Invoke(f) ?? true)) yield return f;
        foreach (var d in dirs)
            foreach (var f in Files(d, depth + 1)) yield return f;
    }

    /// <summary>
    /// Titre lisible tiré du nom du fichier : « Legend of Zelda, The (USA) (Rev 1) [!].nes » → « The Legend of Zelda ».
    /// </summary>
    public static string TitleOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
        name = Tags().Replace(name, " ");
        // Nom d'un site collé au titre (« Jeu-SITE.NET », « Jeu V0 Switch-xci.com ») : retiré.
        name = Site().Replace(name, " ");
        name = MultiSpace().Replace(name, " ").Trim(' ', '-', '.');
        // En fin de titre : numéro de version (« V0 », « 3.0.1 ») ou mot « Switch » laissé par le nom du fichier.
        while (Trailing().Match(name) is { Success: true } t) name = name[..t.Index].Trim(' ', '-', '.');
        // « Titre, The » ou « Titre, The - Sous-titre » : l'article revient devant.
        if (Article().Match(name) is { Success: true } m)
            name = $"{m.Groups["art"].Value} {m.Groups["title"].Value}{m.Groups["rest"].Value}";
        return name.Length == 0 ? Path.GetFileNameWithoutExtension(path) : name;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"[\s\-]?[A-Za-z0-9]+\.(com|net|org|xyz|info|to|io|cc|fr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Site();

    [GeneratedRegex(@"[\s\-](v\d+(\.\d+)*|\d+\.\d+(\.\d+)*|Switch)$", RegexOptions.IgnoreCase)]
    private static partial Regex Trailing();

    [GeneratedRegex(@"^(?<title>.+?), (?<art>The|A|An|Le|La|Les|L')(?<rest>( - .*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();
}
