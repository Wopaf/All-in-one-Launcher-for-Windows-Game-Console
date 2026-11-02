using MaRogAlly.Core;
using MaRogAlly.Models;

namespace MaRogAlly.Scanners;

/// <summary>Jeux ajoutés à la main dans settings.json (émulateurs, jeux sans lanceur…).</summary>
public sealed class ManualScanner : GameScanner
{
    public override Platform Platform => Platform.Manual;

    public override bool IsClientInstalled() => true;

    public override IEnumerable<Game> ScanInstalled(ScanContext ctx)
        => ctx.Settings.ManualGames
            .Where(m => !string.IsNullOrWhiteSpace(m.Exe))
            .Select(m => new Game
            {
                Platform = Platform.Manual,
                PlatformId = Text.Normalize(m.Exe).Replace(' ', '-'),
                Title = string.IsNullOrWhiteSpace(m.Title) ? Path.GetFileNameWithoutExtension(m.Exe) : m.Title,
                IsInstalled = File.Exists(m.Exe),
                InstallDir = Path.GetDirectoryName(m.Exe),
                LaunchExe = m.Exe,
                LaunchArgs = m.Args,
            });

    public override Task<IReadOnlyList<Game>> ScanOwnedAsync(ScanContext ctx, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Game>>([]);
}
