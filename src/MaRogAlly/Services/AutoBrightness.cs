using System.Runtime.InteropServices;
using MaRogAlly.Core;
using Windows.Devices.Sensors;

namespace MaRogAlly.Services;

/// <summary>
/// Luminosité automatique de Windows (« Modifier automatiquement la luminosité quand l'éclairage change ») :
/// un réglage du mode d'alimentation actif, sur secteur comme sur batterie. Sans capteur de lumière, Windows l'ignore.
/// Appels lents : à faire hors du fil de l'interface.
/// </summary>
public static class AutoBrightness
{
    private static readonly Guid VideoGroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid AdaptiveSetting = new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

    public static bool HasSensor
    {
        get
        {
            try { return LightSensor.GetDefault() is not null; }
            catch (Exception ex)
            {
                Log.Error("Recherche du capteur de lumière", ex);
                return false;
            }
        }
    }

    /// <summary>Activée sur secteur ou sur batterie ; null si Windows ne répond pas.</summary>
    public static bool? Read()
    {
        if (ActiveScheme() is not { } scheme) return null;
        var group = VideoGroup;
        var setting = AdaptiveSetting;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var ac) != 0
            | PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var dc) != 0) return null;
        return ac != 0 || dc != 0;
    }

    public static bool Set(bool on)
    {
        if (ActiveScheme() is not { } scheme) return false;
        var group = VideoGroup;
        var setting = AdaptiveSetting;
        var value = on ? 1u : 0u;
        var ok = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, value) == 0
               & PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, value) == 0;
        // Le mode d'alimentation est réappliqué pour que Windows prenne le changement en compte tout de suite.
        return ok && PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
    }

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
