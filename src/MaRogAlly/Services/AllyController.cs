using HidSharp;
using HidSharp.Reports;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

public enum ControlMode { Auto, Gamepad, Desktop }

/// <summary>
/// Mode de la manette intégrée, écrit sur le contrôleur de la ROG Ally et de l'Ally X (commande non documentée par ASUS).
/// Rien n'est écrit sur une autre machine, ni tant que l'utilisateur n'a pas choisi de mode.
/// Armoury Crate peut réécrire son propre mode à tout moment : il garde la main s'il est aussi en mode Auto.
/// </summary>
public static class AllyController
{
    private const int AsusVendor = 0x0B05;
    private const byte InputReport = 0x5A;
    private const byte HardwareGamepad = 1, HardwareMouse = 3;
    private static readonly int[] AllyProducts = [0x1ABE, 0x1B4C];
    private static readonly object WriteLock = new();

    private static readonly Lazy<bool> Available = new(() => Devices().Count > 0);

    /// <summary>Recherche lente la première fois : à lire hors du fil de l'interface.</summary>
    public static bool IsAvailable => Available.Value;

    public static Task WriteAsync(bool gamepad) => Task.Run(() =>
    {
        // Tracé : un changement de mode peut faire perdre à M1 sa touche, le journal dit quand il a eu lieu.
        Log.Info($"Mode de la manette écrit : {(gamepad ? "manette" : "bureau")}");
        byte[] command = [InputReport, 0xD1, 0x01, 0x01, gamepad ? HardwareGamepad : HardwareMouse];
        lock (WriteLock)
            foreach (var device in Devices())
            {
                try
                {
                    using var stream = device.Open();
                    var report = new byte[device.GetMaxFeatureReportLength()];
                    command.CopyTo(report, 0);
                    stream.SetFeature(report);
                }
                catch (Exception ex)
                {
                    Log.Error("Changement du mode de la manette", ex);
                }
            }
    });

    private static List<HidDevice> Devices()
    {
        var found = new List<HidDevice>();
        try
        {
            foreach (var device in DeviceList.Local.GetHidDevices(AsusVendor))
            {
                try
                {
                    if (AllyProducts.Contains(device.ProductID) && device.GetMaxFeatureReportLength() > 0
                        && device.GetReportDescriptor().TryGetReport(ReportType.Feature, InputReport, out _))
                        found.Add(device);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Interface du contrôleur sans ce rapport, ou inaccessible : on passe à la suivante.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recherche de la manette intégrée", ex);
        }
        return found;
    }
}
