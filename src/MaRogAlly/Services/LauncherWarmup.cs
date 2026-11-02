using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MaRogAlly.Core;
using MaRogAlly.Models;
using MaRogAlly.Scanners;
using Microsoft.Win32;

namespace MaRogAlly.Services;

/// <summary>
/// Au démarrage : ouvre en arrière-plan les lanceurs choisis qui ne tournent pas déjà.
/// Leurs fenêtres sont réduites dès qu'elles apparaissent. Pour Steam et Epic Games, la fenêtre principale est
/// ensuite fermée une fois le lanceur prêt (il reste actif dans la zone de notification) ; les autres restent réduits,
/// car fermer leur fenêtre peut les quitter complètement selon leurs réglages.
/// </summary>
public static class LauncherWarmup
{
    /// <param name="UiProcess">Processus de l'interface : la fenêtre principale n'existe qu'une fois celui-ci démarré
    /// (avant, une fenêtre du même nom peut être celle de la mise à jour, qu'il ne faut surtout pas fermer).</param>
    /// <param name="IsMainWindow">Reconnaît la fenêtre à fermer ; null : le lanceur reste simplement réduit.</param>
    private sealed record Target(string Name, IReadOnlyList<string> Processes, string? UiProcess, Func<string, bool>? IsMainWindow);

    /// <summary>Durée maximale de surveillance des fenêtres après le lancement.</summary>
    private static readonly TimeSpan WatchFor = TimeSpan.FromMinutes(2);
    /// <summary>La fenêtre principale doit être stable ce temps-là avant d'être fermée (fin du chargement).</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4);

    /// <summary>Ce qu'il faut exécuter pour ouvrir le lanceur (programme ou adresse), ou null si on ne sait pas l'ouvrir.</summary>
    public static string? Command(PlatformInfo info)
    {
        switch (info.Id)
        {
            case Platform.Steam:
                return SteamScanner.SteamPath() is { } dir && Path.Combine(dir, "steam.exe") is var steam && File.Exists(steam) ? steam : null;
            case Platform.Epic:
                return EpicExe();
            case Platform.Riot:
                return RiotScanner.ClientServices();
            case Platform.Rockstar:
                var rockstar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Rockstar Games\Launcher\Launcher.exe");
                return File.Exists(rockstar) ? rockstar : null;
            default:
                // Adresse d'un lanceur : seulement si Windows la connaît (sinon il demanderait quelle application utiliser).
                if (info.LauncherUri is not { } uri) return null;
                using (var key = Registry.ClassesRoot.OpenSubKey(uri[..uri.IndexOf(':')]))
                    return key is not null ? uri : null;
        }
    }

    /// <param name="refocus">Rend la main à l'application quand la fenêtre réduite était au premier plan.</param>
    public static void Start(IEnumerable<PlatformInfo> platforms, Action refocus)
    {
        var started = new List<Target>();
        foreach (var info in platforms)
        {
            if (info.IsLauncherRunning || Command(info) is not { } command) continue; // déjà lancé : on n'y touche pas
            var target = info.Id switch
            {
                Platform.Steam => new Target(info.Name, ["steam", "steamwebhelper"], "steamwebhelper", title => title == "Steam"),
                Platform.Epic => new Target(info.Name, ["EpicGamesLauncher", "EpicWebHelper"], "EpicWebHelper", title => title.StartsWith("Epic Games Launcher", StringComparison.Ordinal)),
                _ => new Target(info.Name, info.LauncherProcessNames, null, null),
            };
            try
            {
                // « -silent » : démarrage directement dans la zone de notification (Steam, Epic Games).
                var args = target.IsMainWindow is not null ? "-silent" : "";
                Process.Start(new ProcessStartInfo(command, args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
                started.Add(target);
                Log.Info($"Lancement en arrière-plan : {target.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"Lancement de {target.Name} impossible", ex);
            }
        }
        if (started.Count > 0) _ = Task.Run(() => WatchAsync(started, refocus));
    }

    /// <summary>Exécutable du lanceur Epic, d'après son protocole com.epicgames.launcher://.</summary>
    private static string? EpicExe()
    {
        var command = Reg.Get(RegistryHive.CurrentUser, @"Software\Classes\com.epicgames.launcher\shell\open\command", "")
                      ?? Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Classes\com.epicgames.launcher\shell\open\command", "");
        if (command is null) return null;
        var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
        return File.Exists(exe) ? exe : null;
    }

    private static async Task WatchAsync(List<Target> targets, Action refocus)
    {
        var end = DateTime.UtcNow + WatchFor;
        var mainSeen = new Dictionary<IntPtr, DateTime>();
        var done = new HashSet<string>();
        // Lanceurs laissés ouverts : chaque fenêtre n'est réduite qu'une fois, on peut ensuite la rouvrir librement.
        var reduced = new HashSet<IntPtr>();
        while (DateTime.UtcNow < end && done.Count < targets.Count)
        {
            foreach (var t in targets.Where(t => !done.Contains(t.Name)))
            {
                var pids = t.Processes.SelectMany(Process.GetProcessesByName).Select(p => (uint)p.Id).ToHashSet();
                var uiReady = t.UiProcess is not null && Process.GetProcessesByName(t.UiProcess).Length > 0;
                foreach (var (hwnd, title) in VisibleWindows(pids))
                {
                    // Fenêtre principale = bon titre, interface démarrée et grande taille (la fenêtre de mise à jour est petite).
                    if (uiReady && t.IsMainWindow?.Invoke(title) == true && IsLarge(hwnd))
                    {
                        // Fenêtre principale : on attend la fin du chargement, puis on la ferme (le lanceur reste actif).
                        if (!mainSeen.TryGetValue(hwnd, out var since)) mainSeen[hwnd] = since = DateTime.UtcNow;
                        if (DateTime.UtcNow - since >= SettleTime)
                        {
                            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            done.Add(t.Name);
                            Log.Info($"{t.Name} prêt : fenêtre fermée");
                            break;
                        }
                    }
                    if (IsIconic(hwnd) || (t.IsMainWindow is null && !reduced.Add(hwnd))) continue;
                    var wasActive = GetForegroundWindow() == hwnd;
                    ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
                    if (wasActive) refocus();
                }
            }
            await Task.Delay(300);
        }
    }

    private static bool IsLarge(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r) && r.Right - r.Left >= 700 && r.Bottom - r.Top >= 450;

    private static List<(IntPtr Handle, string Title)> VisibleWindows(HashSet<uint> pids)
    {
        var list = new List<(IntPtr, string)>();
        if (pids.Count == 0) return list;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.Length > 0) list.Add((hwnd, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const int SW_SHOWMINNOACTIVE = 7;
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
