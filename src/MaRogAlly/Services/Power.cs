using System.Runtime.InteropServices;

namespace MaRogAlly.Services;

/// <summary>Niveau de batterie de la console, mise en veille et arrêt.</summary>
public static class Power
{
    public readonly record struct Status(bool HasBattery, int Percent, bool Charging);

    public static Status Read()
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
            return new Status(false, 0, false);
        return new Status(true, s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    /// <summary>Glyphe Segoe Fluent correspondant au niveau (0 à 10 dixièmes).</summary>
    public static string Glyph(Status s)
    {
        var tenth = Math.Clamp((int)Math.Round(s.Percent / 10.0), 0, 10);
        if (tenth == 10) return s.Charging ? "" : "";
        return ((char)((s.Charging ? 0xE85A : 0xE850) + tenth)).ToString();
    }

    /// <summary>Mise en veille (pas d'hibernation).</summary>
    public static bool Sleep() => SetSuspendState(false, false, false);

    /// <summary>Arrêt immédiat de l'appareil.</summary>
    public static bool Shutdown()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
