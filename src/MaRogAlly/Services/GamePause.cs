using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>
/// Met un jeu en pause pendant que le centre de contrôle est ouvert : son processus est suspendu (il ne calcule plus rien,
/// comme une console en veille), puis relancé à la fermeture du volet. Le jeu suspendu est noté sur le disque :
/// si l'application s'arrête brutalement, il est relancé à son prochain démarrage au lieu de rester figé.
/// </summary>
public static class GamePause
{
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private static string StateFile => Path.Combine(AppPaths.Root, "paused-game.txt");
    private static int? _paused;

    /// <summary>
    /// Suspend le processus de cette fenêtre et renvoie son numéro, à rendre à <see cref="Resume"/>.
    /// Null si Windows refuse (jeu lancé en administrateur, protégé…) ou si un jeu est déjà en pause.
    /// </summary>
    public static int? Pause(IntPtr window)
    {
        if (_paused is not null) return null;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            // Noté avant de suspendre : un arrêt brutal juste après ne doit pas laisser le jeu figé.
            File.WriteAllText(StateFile, $"{pid}|{process.StartTime.ToFileTimeUtc()}");
            if (!Call(pid, NtSuspendProcess))
            {
                File.Delete(StateFile);
                Log.Info($"Pause du jeu refusée par Windows ({process.ProcessName})");
                return null;
            }
            _paused = (int)pid;
            Log.Info($"Jeu en pause : {process.ProcessName}");
            return (int)pid;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Error("Pause du jeu", ex);
            return null;
        }
    }

    /// <summary>Relance ce jeu s'il est toujours en pause. Un volet rouvert entre-temps a pu en mettre un autre en pause : celui-là n'est pas touché.</summary>
    public static void Resume(int pid)
    {
        if (_paused == pid) ResumeAll();
    }

    /// <summary>Relance le jeu en pause, quel qu'il soit (fermeture de l'application).</summary>
    public static void ResumeAll()
    {
        if (_paused is not { } pid) return;
        _paused = null;
        Call((uint)pid, NtResumeProcess);
        Forget();
        Log.Info("Jeu relancé");
    }

    /// <summary>Au démarrage : relance un jeu resté suspendu par une session précédente arrêtée brutalement.</summary>
    public static void ResumeLeftover()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var parts = File.ReadAllText(StateFile).Split('|');
            Forget();
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var started)) return;
            using var process = Process.GetProcessById((int)pid);
            // Même numéro mais autre processus : Windows réutilise les numéros, on n'y touche pas.
            if (process.StartTime.ToFileTimeUtc() != started) return;
            Call(pid, NtResumeProcess);
            Log.Info($"Jeu resté en pause relancé : {process.ProcessName}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Le jeu a été fermé entre-temps.
        }
    }

    private static void Forget()
    {
        try { File.Delete(StateFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Pause du jeu", ex); }
    }

    private static bool Call(uint pid, Func<IntPtr, int> action)
    {
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return action(handle) == 0; }
        finally { CloseHandle(handle); }
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
