using System.Globalization;
using System.Text.RegularExpressions;

namespace MaRogAlly.Scanners;

/// <summary>Temps de jeu d'un jeu Ankama (clé « jeu-version », ex. dofus-dofus3) et dernière partie.</summary>
public sealed record AnkamaPlay(int Minutes, DateTime LastPlayed);

/// <summary>
/// Le lanceur Ankama n'indique aucun temps de jeu, mais son journal (%APPDATA%\zaap\application*.log) note chaque
/// lancement et chaque fin de partie. On y lit le jeu et l'heure, rien d'autre (le journal cite aussi le compte).
/// Plusieurs comptes ouverts ensemble ne comptent qu'une fois : c'est le temps réel où au moins un tourne.
/// </summary>
public static partial class AnkamaHistory
{
    private enum Kind { Launch, Exit, LauncherRestart }
    private sealed record Event(DateTime Time, Kind Kind, string? Key);

    /// <summary>Au-delà, c'est une fin de partie manquée (ou une machine restée en veille), pas une session.</summary>
    private static readonly TimeSpan LongestSession = TimeSpan.FromHours(24);

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zaap");

    /// <summary>Le journal archivé pèse des dizaines de Mo : on ne relit un fichier que s'il a changé.</summary>
    private static readonly Dictionary<string, (long Size, DateTime Written, List<Event> Events)> Cache = [];

    public static Dictionary<string, AnkamaPlay> Read()
    {
        var plays = new Dictionary<string, AnkamaPlay>();
        if (!Directory.Exists(Folder)) return plays;

        var events = new DirectoryInfo(Folder).GetFiles("application*.log").OrderBy(f => f.LastWriteTimeUtc).SelectMany(Events).ToList();
        foreach (var key in events.Where(e => e.Key is not null).Select(e => e.Key!).Distinct())
        {
            var (minutes, last) = Total(events, key);
            if (last is { } played) plays[key] = new AnkamaPlay(minutes, played);
        }
        return plays;
    }

    /// <summary>Additionne les périodes où au moins une partie tourne ; une partie restée ouverte à la fermeture du lanceur est ignorée.</summary>
    private static (int Minutes, DateTime? Last) Total(List<Event> events, string key)
    {
        var total = TimeSpan.Zero;
        var open = 0;
        DateTime start = default;
        DateTime? last = null;
        foreach (var e in events)
        {
            if (e.Kind == Kind.LauncherRestart) open = 0;
            else if (e.Key != key) continue;
            else if (e.Kind == Kind.Launch)
            {
                if (open++ == 0) start = e.Time;
                last = e.Time;
            }
            else if (open > 0 && --open == 0 && e.Time - start is var played && played < LongestSession) total += played;
        }
        return ((int)total.TotalMinutes, last);
    }

    private static List<Event> Events(FileInfo file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file.FullName, out var known) && known.Size == file.Length && known.Written == file.LastWriteTimeUtc) return known.Events;
            var events = Parse(file);
            Cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, events);
            return events;
        }
    }

    private static List<Event> Parse(FileInfo file)
    {
        var events = new List<Event>();
        try
        {
            // Le lanceur tient son journal ouvert.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("[KPI] gameLaunch", StringComparison.Ordinal))
                {
                    if (TimeOf(line) is { } time && reader.ReadLine() is { } details && LaunchDetails().Match(details) is { Success: true } m)
                        events.Add(new Event(time, Kind.Launch, $"{m.Groups[1].Value}-{m.Groups[2].Value}".ToLowerInvariant()));
                }
                else if (line.Contains("[RELEASE] Release exited", StringComparison.Ordinal))
                {
                    // { puis name: '…', puis release: '…', sur des lignes séparées.
                    reader.ReadLine();
                    var name = Quoted().Match(reader.ReadLine() ?? "");
                    var release = Quoted().Match(reader.ReadLine() ?? "");
                    if (TimeOf(line) is { } time && name.Success && release.Success)
                        events.Add(new Event(time, Kind.Exit, $"{name.Groups[1].Value}-{release.Groups[1].Value}".ToLowerInvariant()));
                }
                else if (line.Contains("[MAIN] App ready", StringComparison.Ordinal) || line.Contains("[APP] app.exit", StringComparison.Ordinal))
                {
                    if (TimeOf(line) is { } time) events.Add(new Event(time, Kind.LauncherRestart, null));
                }
            }
        }
        catch (IOException) { }
        return events;
    }

    /// <summary>Début de ligne : « 27/07/2026, 18:26:44.648 - info: … » (selon la langue de Windows).</summary>
    private static DateTime? TimeOf(string line)
    {
        var end = line.IndexOf(" - ", StringComparison.Ordinal);
        if (end <= 0) return null;
        var text = line[..end];
        if (text.LastIndexOf('.') is var dot and > 0) text = text[..dot];
        if (DateTime.TryParseExact(text, "dd/MM/yyyy, HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out time) ? time : null;
    }

    [GeneratedRegex(@"gameUid: '([^']+)', releaseName: '([^']+)'")]
    private static partial Regex LaunchDetails();

    [GeneratedRegex(@"^\s*\w+: '([^']+)'")]
    private static partial Regex Quoted();
}
