using Microsoft.Win32;

namespace MaRogAlly.Scanners;

/// <summary>Accès au registre (vues 64 et 32 bits, HKLM et HKCU).</summary>
public static class Reg
{
    public sealed record UninstallEntry(
        string KeyName,
        string DisplayName,
        string? Publisher,
        string? InstallLocation,
        string? DisplayIcon,
        string? UninstallString);

    public static string? Get(RegistryHive hive, string path, string name)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                if (key?.GetValue(name) is { } v) return v.ToString();
            }
            catch { /* clé inaccessible */ }
        }
        return null;
    }

    /// <summary>Sous-clés d'un chemin, avec leurs valeurs chaînes.</summary>
    public static IEnumerable<(string Name, Dictionary<string, string> Values)> SubKeys(RegistryHive hive, string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            List<(string, Dictionary<string, string>)> batch = [];
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (!seen.Add(sub)) continue;
                    using var sk = key.OpenSubKey(sub);
                    if (sk is null) continue;
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var n in sk.GetValueNames())
                        if (sk.GetValue(n) is { } v) values[n] = v.ToString() ?? "";
                    batch.Add((sub, values));
                }
            }
            catch { /* clé inaccessible */ }
            foreach (var item in batch) yield return item;
        }
    }

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Programmes déclarés dans « Applications installées ».</summary>
    public static List<UninstallEntry> UninstallEntries()
    {
        var list = new List<UninstallEntry>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var (name, v) in SubKeys(hive, UninstallPath))
        {
            if (!v.TryGetValue("DisplayName", out var display) || string.IsNullOrWhiteSpace(display)) continue;
            list.Add(new UninstallEntry(
                name,
                display.Trim(),
                v.GetValueOrDefault("Publisher"),
                v.GetValueOrDefault("InstallLocation")?.Trim('"', ' '),
                v.GetValueOrDefault("DisplayIcon"),
                v.GetValueOrDefault("UninstallString")));
        }
        return list;
    }

    /// <summary>Extrait le chemin d'un .exe d'une valeur DisplayIcon (« "C:\x.exe",0 »).</summary>
    public static string? ExeFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        var comma = s.LastIndexOf(',');
        if (comma > 2 && !s[comma..].Contains('\\')) s = s[..comma];
        s = s.Trim('"', ' ');
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s) ? s : null;
    }
}
