using MaRogAlly.Core;
using Windows.Management.Deployment;

namespace MaRogAlly.Services;

/// <summary>Résultat de la recherche Windows Update (pilotes compris).</summary>
public sealed record WindowsUpdateStatus(List<string> Titles, bool RebootRequired, string? Error, DateTime CheckedAt)
{
    public int Count => Titles.Count;
}

/// <summary>Application ASUS intégrée à la ROG Ally (Armoury Crate SE), si elle est installée.</summary>
public sealed record AsusAppInfo(string Name, string Version, string FamilyName);

/// <summary>Dernier résultat connu : affiché tout de suite au démarrage, en attendant la vérification en cours.</summary>
public sealed record UpdateSnapshot(WindowsUpdateStatus? Windows, AsusAppInfo? Asus)
{
    private static string CachePath => Path.Combine(AppPaths.Root, "updates.json");
    public static UpdateSnapshot? Load() => Json.Load<UpdateSnapshot>(CachePath);
    public void Save() => Json.Save(CachePath, this);
}

/// <summary>
/// Mises à jour : Windows Update via l'agent Windows (COM « Microsoft.Update.Session », sans droits admin),
/// et version d'Armoury Crate, dont le centre de mise à jour (BIOS, pilotes, firmware) n'est consultable que dans l'app.
/// </summary>
public static class UpdateChecker
{
    public static Task<WindowsUpdateStatus> CheckWindowsAsync() => Task.Run(() =>
    {
        var titles = new List<string>();
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                              ?? throw new InvalidOperationException("Agent Windows Update introuvable");
            dynamic session = Activator.CreateInstance(sessionType)!;
            session.ClientApplicationID = "Ma ROG Ally";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            foreach (dynamic update in result.Updates) titles.Add((string)update.Title);

            var reboot = false;
            if (Type.GetTypeFromProgID("Microsoft.Update.SystemInfo") is { } infoType)
            {
                dynamic info = Activator.CreateInstance(infoType)!;
                reboot = (bool)info.RebootRequired;
            }
            Log.Info($"Windows Update : {titles.Count} mise(s) à jour disponible(s){(reboot ? ", redémarrage requis" : "")}");
            return new WindowsUpdateStatus(titles, reboot, null, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Error("Vérification Windows Update impossible", ex);
            return new WindowsUpdateStatus(titles, false, "Vérification impossible pour le moment", DateTime.Now);
        }
    });

    /// <summary>Armoury Crate SE (ROG Ally) ou Armoury Crate (autres PC ASUS).</summary>
    public static AsusAppInfo? FindArmouryCrate()
    {
        try
        {
            var pkg = new PackageManager().FindPackagesForUser("")
                .Where(p => p.Id.Name.Contains("ArmouryCrate", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Id.Name.Contains("SE", StringComparison.Ordinal))
                .FirstOrDefault();
            if (pkg is null) return null;
            var v = pkg.Id.Version;
            var name = pkg.Id.Name.EndsWith("SE", StringComparison.OrdinalIgnoreCase) ? "Armoury Crate SE" : "Armoury Crate";
            return new AsusAppInfo(name, $"{v.Major}.{v.Minor}.{v.Build}", pkg.Id.FamilyName);
        }
        catch (Exception ex)
        {
            Log.Error("Recherche d'Armoury Crate", ex);
            return null;
        }
    }

    /// <summary>Ouvre Armoury Crate (son centre de mise à jour est dans l'app).</summary>
    public static async Task<bool> LaunchAsync(AsusAppInfo app)
    {
        try
        {
            var pkg = new PackageManager().FindPackagesForUser("").FirstOrDefault(p => p.Id.FamilyName == app.FamilyName);
            if (pkg is null) return false;
            var entries = await pkg.GetAppListEntriesAsync();
            return entries.Count > 0 && await entries[0].LaunchAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture d'Armoury Crate", ex);
            return false;
        }
    }
}
