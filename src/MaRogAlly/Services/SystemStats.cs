using System.Runtime.InteropServices;
using MaRogAlly.Core;
using MaRogAlly.Scanners;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MaRogAlly.Services;

/// <summary>Valeurs à l'instant t ; null quand la machine ne les donne pas.</summary>
public sealed record PerfSample(double? Cpu, int? CpuTemp, double? Gpu, int? GpuTemp, long? VramUsed, long? VramTotal, double RamUsed, double RamTotal);

/// <summary>
/// Utilisation du processeur, de la carte graphique et de la mémoire (compteurs de Windows, ceux du Gestionnaire des tâches),
/// et températures sur une machine ASUS (lecture seule, par son pilote ATK). À lire hors du fil de l'interface.
/// </summary>
public sealed class SystemStats : IDisposable
{
    private const uint FormatDouble = 0x200, NoCap100 = 0x8000;
    private IntPtr _query;
    private readonly IntPtr _cpu, _gpu, _vram;
    private readonly long? _vramTotal = ReadVramTotal();

    public SystemStats()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu);
        PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpu);
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _vram);
        PdhCollectQueryData(_query);
    }

    public PerfSample Read()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        return new PerfSample(
            Percent(Values(_cpu).Sum()),
            AsusTemperature(0x00120094),
            Percent(Values(_gpu).Sum()),
            AsusTemperature(0x00120097),
            Values(_vram) is { Count: > 0 } vram ? (long)vram.Max() : null,
            _vramTotal,
            memory.Total - memory.Available,
            memory.Total);
    }

    private static double? Percent(double value) => Math.Clamp(value, 0, 100);

    /// <summary>Valeurs d'un compteur, une par instance (processus, carte graphique…).</summary>
    private static List<double> Values(IntPtr counter)
    {
        var values = new List<double>();
        if (counter == IntPtr.Zero) return values;
        uint size = 0, count = 0;
        PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, ref count, IntPtr.Zero);
        if (size == 0) return values;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, ref count, buffer) != 0) return values;
            // PDH_FMT_COUNTERVALUE_ITEM : nom (pointeur), état (4 octets, puis 4 d'alignement), valeur.
            var item = IntPtr.Size + 16;
            for (var i = 0; i < count; i++)
            {
                var at = buffer + i * item;
                if ((uint)Marshal.ReadInt32(at, IntPtr.Size) <= 1)
                    values.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at, IntPtr.Size + 8)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return values;
    }

    /// <summary>Plus grande mémoire annoncée par une carte graphique (ce réglage dépend du BIOS sur une console portable).</summary>
    private static long? ReadVramTotal()
    {
        long? best = null;
        try
        {
            foreach (var (_, values) in Reg.SubKeys(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                if (values.TryGetValue("HardwareInformation.qwMemorySize", out var text) && long.TryParse(text, out var size) && size > (best ?? 0))
                    best = size;
        }
        catch (Exception ex)
        {
            Log.Error("Taille de la mémoire graphique", ex);
        }
        return best;
    }

    // ---- Températures : méthode DSTS du pilote ATK des machines ASUS ----

    private const uint AtkIoControl = 0x0022240C, AtkGetStatus = 0x53545344;
    private SafeFileHandle? _atk;
    private bool _atkTried;

    private int? AsusTemperature(uint sensor)
    {
        if (!_atkTried)
        {
            _atkTried = true;
            var handle = CreateFileW(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) handle.Dispose();
            else _atk = handle;
        }
        if (_atk is null) return null;

        var input = new byte[16];
        BitConverter.GetBytes(AtkGetStatus).CopyTo(input, 0);
        BitConverter.GetBytes(8u).CopyTo(input, 4);
        BitConverter.GetBytes(sensor).CopyTo(input, 8);
        var output = new byte[16];
        if (!DeviceIoControl(_atk, AtkIoControl, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero)) return null;
        var value = BitConverter.ToInt32(output, 0) - 65536;
        return value is > 0 and < 125 ? value : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _atk?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
