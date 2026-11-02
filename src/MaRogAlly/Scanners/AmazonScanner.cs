using MaRogAlly.Core;
using MaRogAlly.Models;
using Microsoft.Data.Sqlite;

namespace MaRogAlly.Scanners;

public sealed class AmazonScanner : GameScanner
{
    public override Platform Platform => Platform.Amazon;

    private static string SqlDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Amazon Games\Data\Games\Sql");

    public override bool IsClientInstalled()
        => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Amazon Games\App"));

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
        => ReadDbSet(Path.Combine(SqlDir, "GameInstallInfo.sqlite"))
            .Where(r => r.GetValueOrDefault("Installed") == "1" && r.ContainsKey("Id"))
            .Select(r => new Game
            {
                Platform = Platform.Amazon,
                PlatformId = r["Id"],
                Title = r.GetValueOrDefault("ProductTitle") ?? r["Id"],
                IsInstalled = true,
                InstallDir = r.GetValueOrDefault("InstallDirectory"),
                LaunchUri = $"amazon-games://play/{r["Id"]}",
            })
            .ToList();

    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
    {
        IReadOnlyList<Game> owned = ReadDbSet(Path.Combine(SqlDir, "GameProductInfo.sqlite"))
            .Select(r => (Id: r.GetValueOrDefault("ProductIdStr") ?? r.GetValueOrDefault("Id"), Row: r))
            .Where(x => x.Id is not null && x.Row.ContainsKey("ProductTitle"))
            .Select(x => new Game
            {
                Platform = Platform.Amazon,
                PlatformId = x.Id!,
                Title = x.Row["ProductTitle"],
                CoverUrl = x.Row.GetValueOrDefault("ProductIconUrl"),
                HeroUrl = x.Row.GetValueOrDefault("ProductLogoUrl"),
                InstallUri = $"amazon-games://play/{x.Id}",
            })
            .ToList();
        return Task.FromResult(owned.Count > 0 ? owned : OwnedFromGalaxy(ctx));
    }

    /// <summary>Lit la table DbSet sans présupposer ses colonnes (le schéma varie selon les versions).</summary>
    private static List<Dictionary<string, string>> ReadDbSet(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return rows;
        try
        {
            using var cn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT * FROM DbSet";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i)) row[r.GetName(i)] = Convert.ToString(r.GetValue(i)) ?? "";
                rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Base Amazon illisible ({path})", ex);
        }
        return rows;
    }
}
