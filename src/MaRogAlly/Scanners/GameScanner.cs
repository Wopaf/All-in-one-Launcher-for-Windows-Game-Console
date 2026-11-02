using System.Net.Http;
using MaRogAlly.Models;

namespace MaRogAlly.Scanners;

/// <summary>Contexte partagé par les scanners pendant un scan.</summary>
public sealed record ScanContext(AppSettings Settings, HttpClient Http, GalaxyLibrary Galaxy);

public abstract class GameScanner
{
    public abstract Platform Platform { get; }

    /// <summary>Le lanceur de la plateforme est-il installé sur cette machine ?</summary>
    public abstract bool IsClientInstalled();

    /// <summary>Jeux installés, lus localement (rapide, hors ligne).</summary>
    public abstract IEnumerable<Game> ScanInstalled(ScanContext ctx);

    /// <summary>Jeux possédés (installés ou non), depuis un cache local ou une API en ligne.</summary>
    public virtual Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
        => Task.FromResult(OwnedFromGalaxy(ctx));

    protected IReadOnlyList<Game> OwnedFromGalaxy(ScanContext ctx)
        => ctx.Galaxy.ForPrefixes(GalaxyLibrary.PrefixesFor(Platform))
            .Select(g => new Game
            {
                Platform = Platform,
                PlatformId = g.Id,
                Title = g.Title,
                CoverUrl = g.Cover,
                HeroUrl = g.Background,
                InstallUri = PlatformInfo.Of(Platform).LauncherUri,
            })
            .ToList();
}
