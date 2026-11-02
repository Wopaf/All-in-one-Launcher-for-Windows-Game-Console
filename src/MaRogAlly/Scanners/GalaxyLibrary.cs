using System.IO;
using System.Text.Json;
using MaRogAlly.Core;
using Microsoft.Data.Sqlite;

namespace MaRogAlly.Scanners;

/// <summary>
/// Lit la base locale de GOG Galaxy 2. Elle contient la bibliothèque GOG, mais aussi celle des
/// autres plateformes que l'utilisateur y a connectées (Xbox, EA, Ubisoft, Battle.net…).
/// </summary>
public sealed class GalaxyLibrary
{
    public sealed record Entry(string Prefix, string Id, string Title, string? Cover, string? Background);

    private readonly List<Entry> _entries = [];

    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        @"GOG.com\Galaxy\storage\galaxy-2.0.db");

    /// <summary>Base vide, quand les jeux possédés ne sont pas utiles (lecture des seuls jeux installés).</summary>
    public static readonly GalaxyLibrary Empty = new();

    public static GalaxyLibrary Load()
    {
        var lib = new GalaxyLibrary();
        if (!File.Exists(DbPath)) return lib;

        // Copie pour ne pas gêner Galaxy, qui garde la base ouverte.
        var tmp = Path.Combine(Path.GetTempPath(), "marogally-galaxy.db");
        try
        {
            File.Copy(DbPath, tmp, true);
            if (File.Exists(DbPath + "-wal")) File.Copy(DbPath + "-wal", tmp + "-wal", true);
            else if (File.Exists(tmp + "-wal")) File.Delete(tmp + "-wal");

            using var cn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False");
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = """
                SELECT lr.releaseKey, t.type, gp.value
                FROM LibraryReleases lr
                JOIN GamePieces gp ON gp.releaseKey = lr.releaseKey
                JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId
                WHERE t.type IN ('title', 'originalTitle', 'originalImages')
                """;

            var titles = new Dictionary<string, string>();
            var images = new Dictionary<string, (string? Cover, string? Bg)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var key = r.GetString(0);
                    var type = r.GetString(1);
                    using var doc = JsonDocument.Parse(r.GetString(2));
                    var root = doc.RootElement;
                    switch (type)
                    {
                        case "title" or "originalTitle" when root.TryGetProperty("title", out var t) && t.GetString() is { } title:
                            if (type == "title" || !titles.ContainsKey(key)) titles[key] = title;
                            break;
                        case "originalImages":
                            images[key] = (Str(root, "verticalCover"), Str(root, "background"));
                            break;

                    }
                }
            }

            foreach (var (key, title) in titles)
            {
                var sep = key.IndexOf('_');
                if (sep <= 0) continue;
                images.TryGetValue(key, out var img);
                lib._entries.Add(new Entry(key[..sep], key[(sep + 1)..], title, img.Cover, img.Bg));
            }
            Log.Info($"GOG Galaxy : {lib._entries.Count} jeux dans la base");
            lib.ReadPlayTimes(cn);
        }
        catch (Exception ex)
        {
            Log.Error("Lecture de la base GOG Galaxy impossible", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tmp);
            TryDelete(tmp + "-wal");
        }
        return lib;
    }

    public IEnumerable<Entry> ForPrefixes(string[] prefixes)
        => _entries.Where(e => prefixes.Contains(e.Prefix, StringComparer.OrdinalIgnoreCase));

    /// <summary>Préfixes des clés Galaxy (« steam_123 », « epic_xxx »…) pour chaque plateforme.</summary>
    public static string[] PrefixesFor(Models.Platform p) => p switch
    {
        Models.Platform.Steam => ["steam"],
        Models.Platform.Epic => ["epic"],
        Models.Platform.Gog => ["gog"],
        Models.Platform.Ea => ["origin", "ea"],
        Models.Platform.Ubisoft => ["uplay", "ubisoft"],
        Models.Platform.BattleNet => ["battlenet"],
        Models.Platform.Xbox => ["xboxone", "xbox"],
        Models.Platform.Amazon => ["amazon"],
        Models.Platform.Rockstar => ["rockstar"],
        _ => [],
    };

    // ---- Temps de jeu suivis par Galaxy (toutes plateformes connectées) ----

    private readonly Dictionary<string, (int Minutes, DateTime? Last)> _times = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Temps de jeu et dernière partie connus de Galaxy pour un jeu, s'il y en a.</summary>
    public (int Minutes, DateTime? Last)? PlayTime(Models.Platform p, string id)
    {
        foreach (var prefix in PrefixesFor(p))
            if (_times.TryGetValue($"{prefix}_{id}", out var t)) return t;
        return null;
    }

    private void ReadPlayTimes(SqliteConnection cn)
    {
        // Ces tables n'existent pas forcément selon la version de Galaxy : chaque lecture est facultative.
        TryRead(cn, "SELECT releaseKey, minutesInGame FROM GameTimes", r =>
        {
            var key = r.GetString(0);
            var minutes = r.IsDBNull(1) ? 0 : r.GetInt32(1);
            var cur = _times.GetValueOrDefault(key);
            _times[key] = (Math.Max(cur.Minutes, minutes), cur.Last);
        });
        TryRead(cn, "SELECT gameReleaseKey, lastPlayedDate FROM LastPlayedDates", r =>
        {
            if (r.IsDBNull(1) || !DateTime.TryParse(r.GetString(1), out var date)) return;
            var key = r.GetString(0);
            var cur = _times.GetValueOrDefault(key);
            _times[key] = (cur.Minutes, cur.Last is { } l && l > date ? l : date);
        });
    }

    private static void TryRead(SqliteConnection cn, string sql, Action<SqliteDataReader> row)
    {
        try
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) row(r);
        }
        catch (Exception ex)
        {
            Log.Error($"Galaxy : lecture facultative ignorée ({sql})", ex);
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
