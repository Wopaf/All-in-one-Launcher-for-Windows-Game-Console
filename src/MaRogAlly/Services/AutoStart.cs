using MaRogAlly.Core;
using Microsoft.Win32;

namespace MaRogAlly.Services;

/// <summary>Lancement à l'ouverture de session (clé Run de l'utilisateur, sans droits admin).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MaRogAlly";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(Name, $"\"{exe}\" --startup");
            else
                key.DeleteValue(Name, false);
        }
        catch (Exception ex)
        {
            Log.Error("Réglage du démarrage automatique impossible", ex);
        }
    }
}
