using MaRogAlly.Core;
using MaRogAlly.Models;
using Microsoft.Data.Sqlite;

namespace MaRogAlly.Scanners;

/// <summary>Application itch : base « butler.db » (jeux installés = caves, possédés = download_keys).</summary>
public sealed class ItchScanner : GameScanner
{
    public override Platform Platform => Platform.Itch;

    private static string Db => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"itch\db\butler.db");

    public override bool IsClientInstalled()
        => File.Exists(Db) || Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "itch"));

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
        => Query("""
            SELECT c.id, g.id, g.title, il.path, c.install_folder_name
            FROM caves c
            JOIN games g ON g.id = c.game_id
            LEFT JOIN install_locations il ON il.id = c.install_location_id
            """,
            r => new Game
            {
                Platform = Platform.Itch,
                PlatformId = r.GetValue(1).ToString()!,
                Title = r.GetString(2),
                IsInstalled = true,
                InstallDir = r.IsDBNull(3) ? null : Path.Combine(r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)),
                LaunchUri = $"itch://caves/{r.GetString(0)}/launch",
            });

    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        IReadOnlyList<Game> owned = Query("""
            SELECT DISTINCT g.id, g.title, g.cover_url
            FROM download_keys k
            JOIN games g ON g.id = k.game_id
            """,
            r => new Game
            {
                Platform = Platform.Itch,
                PlatformId = r.GetValue(0).ToString()!,
                Title = r.GetString(1),
                CoverUrl = r.IsDBNull(2) ? null : r.GetString(2),
                InstallUri = $"itch://games/{r.GetValue(0)}",
            });
        return Task.FromResult(owned);
    }

    private static List<Game> Query(string sql, Func<SqliteDataReader, Game> map)
    {
        var list = new List<Game>();
        if (!File.Exists(Db)) return list;
        try
        {
            using var cn = new SqliteConnection($"Data Source={Db};Mode=ReadOnly;Pooling=False");
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(map(r));
        }
        catch (Exception ex)
        {
            Log.Error("Base itch illisible", ex);
        }
        return list;
    }
}
