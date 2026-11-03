using MaRogAlly.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace MaRogAlly.Services;

public sealed record BluetoothEntry(string Id, string Name, bool Paired, bool Connected);

/// <summary>Appareils Bluetooth : associés et à portée (classiques et basse consommation), association, dissociation.</summary>
public sealed class BluetoothService
{
    private const string Classic = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", LowEnergy = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    private readonly Dictionary<string, DeviceInformation> _devices = [];
    private DeviceWatcher? _watcher;

    public Radio? Radio { get; private init; }
    public bool IsOn => Radio?.State == RadioState.On;

    public static async Task<BluetoothService> OpenAsync() => new() { Radio = await Radios.FindAsync(RadioKind.Bluetooth) };

    /// <summary>Lance la recherche : la liste se remplit au fil de l'eau (lue par <see cref="Entries"/>).</summary>
    public void Start()
    {
        if (_watcher is not null) return;
        _watcher = DeviceInformation.CreateWatcher(
            $"(System.Devices.Aep.ProtocolId:=\"{Classic}\") OR (System.Devices.Aep.ProtocolId:=\"{LowEnergy}\")",
            [IsConnectedProperty, "System.Devices.Aep.IsPaired"], DeviceInformationKind.AssociationEndpoint);
        _watcher.Added += (_, d) => { lock (_devices) _devices[d.Id] = d; };
        _watcher.Updated += (_, u) => { lock (_devices) if (_devices.TryGetValue(u.Id, out var d)) d.Update(u); };
        _watcher.Removed += (_, u) => { lock (_devices) _devices.Remove(u.Id); };
        _watcher.Start();
    }

    public void Stop()
    {
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Arrêt de la recherche Bluetooth", ex);
        }
        _watcher = null;
        lock (_devices) _devices.Clear();
    }

    /// <summary>Appareils nommés, par nom ; un appareil vu en classique et en basse consommation n'apparaît qu'une fois.</summary>
    public List<BluetoothEntry> Entries()
    {
        lock (_devices)
            return _devices.Values
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new BluetoothEntry(d.Id, d.Name, d.Pairing.IsPaired, d.Properties.TryGetValue(IsConnectedProperty, out var c) && c is true))
                .GroupBy(e => e.Name)
                .Select(g => g.OrderByDescending(e => e.Connected).ThenByDescending(e => e.Paired).First())
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    /// <summary>Associe un appareil.</summary>
    /// <param name="ask">Reçoit le type de demande et le code à montrer ; renvoie la réponse (le code saisi, ou "") ou null pour refuser.</param>
    public async Task<bool> PairAsync(string id, Func<DevicePairingKinds, string, Task<string?>> ask)
    {
        DeviceInformation? device;
        lock (_devices) _devices.TryGetValue(id, out device);
        if (device is null) return false;

        var pairing = device.Pairing.Custom;
        async void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                if (await ask(e.PairingKind, e.Pin ?? "") is not { } answer) return;
                if (e.PairingKind == DevicePairingKinds.ProvidePin) e.Accept(answer);
                else e.Accept();
            }
            catch (Exception ex)
            {
                Log.Error("Association Bluetooth", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }
        pairing.PairingRequested += OnRequested;
        try
        {
            var result = await pairing.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin,
                DevicePairingProtectionLevel.Default);
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        finally
        {
            pairing.PairingRequested -= OnRequested;
        }
    }

    public async Task<bool> UnpairAsync(string id)
    {
        DeviceInformation? device;
        lock (_devices) _devices.TryGetValue(id, out device);
        if (device is null) return false;
        var result = await device.Pairing.UnpairAsync();
        return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
    }

    /// <summary>
    /// Demande à l'appareil de se connecter, puis attend de le voir connecté. Windows n'offre pas d'action « connecter » générale :
    /// on interroge ses services, ce qui réveille la liaison sur beaucoup d'appareils, sans garantie (surtout pour l'audio).
    /// </summary>
    public async Task<bool> ConnectAsync(string id)
    {
        try
        {
            using var classic = await BluetoothDevice.FromIdAsync(id);
            if (classic is not null) await classic.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
            else
            {
                using var lowEnergy = await BluetoothLEDevice.FromIdAsync(id);
                if (lowEnergy is not null) await lowEnergy.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            }
            for (var i = 0; i < 16; i++)
            {
                if (Entries().Any(e => e.Id == id && e.Connected)) return true;
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Connexion Bluetooth", ex);
        }
        return false;
    }
}
