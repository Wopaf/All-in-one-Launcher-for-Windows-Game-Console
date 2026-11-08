using System.Diagnostics;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>
/// Ferme Armoury Crate au lancement de l'application (réglage) : son volet et ses boutons ne passent plus devant.
/// Ses services Windows tournent avec les droits du système : sans droits d'administrateur, ils ne peuvent pas être
/// arrêtés, et peuvent relancer une partie d'Armoury Crate (le journal note ce qui a été fermé ou refusé).
/// </summary>
public static class ArmouryCrate
{
    /// <summary>Ferme les processus dont le nom commence par « Armoury » ; à appeler hors du fil de l'interface.</summary>
    public static void CloseAll()
    {
        var closed = new List<string>();
        var refused = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (!name.StartsWith("Armoury", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    process.Kill();
                    closed.Add(name);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    refused.Add(name);
                }
            }
        }
        if (closed.Count == 0 && refused.Count == 0) Log.Info("Armoury Crate : aucun processus en cours");
        else Log.Info($"Armoury Crate : fermé {(closed.Count > 0 ? string.Join(", ", closed) : "rien")}"
                      + (refused.Count > 0 ? $" ; refusé par Windows (service système) : {string.Join(", ", refused)}" : ""));
    }
}
