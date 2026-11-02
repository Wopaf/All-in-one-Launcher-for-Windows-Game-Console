using System.Management;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>Luminosité de l'écran intégré (consoles portables et PC portables), par WMI. Sans objet sur un écran externe.</summary>
public static class ScreenBrightness
{
    private const string Scope = @"root\WMI";
    private static int _pending = -1, _writing;

    /// <summary>Niveau actuel en pourcentage ; null si cet écran ne se règle pas ainsi.</summary>
    public static Task<int?> ReadAsync() => Task.Run<int?>(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var monitor in searcher.Get()) return (byte)monitor["CurrentBrightness"];
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { }
        return null;
    });

    /// <summary>Applique le niveau sans bloquer : pendant un glissement, seule la dernière valeur compte.</summary>
    public static void Set(int percent)
    {
        Interlocked.Exchange(ref _pending, percent);
        if (Interlocked.CompareExchange(ref _writing, 1, 0) == 0) Task.Run(Drain);
    }

    private static void Drain()
    {
        int value;
        while ((value = Interlocked.Exchange(ref _pending, -1)) >= 0) Write(value);
        Interlocked.Exchange(ref _writing, 0);
        if (Volatile.Read(ref _pending) >= 0 && Interlocked.CompareExchange(ref _writing, 1, 0) == 0) Drain();
    }

    private static void Write(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject monitor in searcher.Get()) monitor.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
        }
        catch (Exception ex) { Log.Error("Réglage de la luminosité", ex); }
    }
}
