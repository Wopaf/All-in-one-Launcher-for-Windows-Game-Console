using System.Runtime.InteropServices;

namespace MaRogAlly.Services;

public sealed record DisplayMode(int Width, int Height, int Hertz);

/// <summary>
/// Résolution et fréquence de l'écran principal. Un essai n'est pas enregistré : tant qu'il n'est pas confirmé,
/// <see cref="Restore"/> revient aux réglages de Windows, pour ne jamais rester bloqué sur un mode inutilisable.
/// </summary>
public static class DisplayModes
{
    private const int CurrentSettings = -1, FieldsWidthHeightHertz = 0x00080000 | 0x00100000 | 0x00400000;
    private const uint UpdateRegistry = 1;

    public static DisplayMode? Current()
    {
        var mode = New();
        return EnumDisplaySettingsW(null, CurrentSettings, ref mode) ? new DisplayMode(mode.Width, mode.Height, mode.Frequency) : null;
    }

    /// <summary>Modes proposés par l'écran (couleurs 32 bits, 800×600 au moins), du plus grand au plus petit.</summary>
    public static List<DisplayMode> All()
    {
        var modes = new HashSet<DisplayMode>();
        var mode = New();
        for (var i = 0; EnumDisplaySettingsW(null, i, ref mode); i++)
            if (mode.BitsPerPel == 32 && mode.Width >= 800 && mode.Height >= 600) modes.Add(new DisplayMode(mode.Width, mode.Height, mode.Frequency));
        return modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.Hertz).ToList();
    }

    /// <summary>Essaie un mode, sans l'enregistrer.</summary>
    public static bool Try(DisplayMode target) => Apply(target, 0);

    /// <summary>Enregistre le mode essayé.</summary>
    public static bool Keep(DisplayMode target) => Apply(target, UpdateRegistry);

    /// <summary>Revient aux réglages enregistrés dans Windows.</summary>
    public static void Restore() => ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    private static bool Apply(DisplayMode target, uint flags)
    {
        var mode = New();
        if (!EnumDisplaySettingsW(null, CurrentSettings, ref mode)) return false;
        mode.Width = target.Width;
        mode.Height = target.Height;
        mode.Frequency = target.Hertz;
        mode.Fields = FieldsWidthHeightHertz;
        return ChangeDisplaySettingsExW(null, ref mode, IntPtr.Zero, flags, IntPtr.Zero) == 0;
    }

    private static DevMode New() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY;
        public int Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public int BitsPerPel, Width, Height, Flags, Frequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int index, ref DevMode mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string? device, ref DevMode mode, IntPtr window, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string? device, IntPtr noMode, IntPtr window, uint flags, IntPtr param);
}
