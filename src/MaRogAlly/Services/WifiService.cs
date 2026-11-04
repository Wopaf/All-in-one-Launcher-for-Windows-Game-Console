using System.Diagnostics;
using MaRogAlly.Core;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace MaRogAlly.Services;

/// <summary>Interrupteurs Wi-Fi et Bluetooth de Windows.</summary>
public static class Radios
{
    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex)
        {
            Log.Error($"Lecture de l'état {kind}", ex);
            return null;
        }
    }

    /// <summary>Toutes les radios de la machine ; liste vide si Windows en refuse l'accès.</summary>
    public static async Task<List<Radio>> AllAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return [];
            return (await Radio.GetRadiosAsync()).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des connexions sans fil", ex);
            return [];
        }
    }

    public static async Task<bool> IsOnAsync(RadioKind kind) => (await FindAsync(kind))?.State == RadioState.On;

    /// <summary>Faux si Windows a refusé (ex. mode avion imposé par le système).</summary>
    public static async Task<bool> SetAsync(Radio radio, bool on)
    {
        try
        {
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error($"Changement de l'état {radio.Kind}", ex);
            return false;
        }
    }
}

public enum WifiSecurity { Open, Password, Enterprise }

/// <param name="Raw">Réseau de Windows (null : réseau d'exemple, « --network demo »).</param>
public sealed record WifiNetwork(string Ssid, byte Bars, WifiSecurity Security, bool Saved, bool Connected, WiFiAvailableNetwork? Raw)
{
    public bool IsHidden => Ssid.Length == 0;
}

/// <summary>Carte Wi-Fi de la machine : recherche des réseaux, connexion, déconnexion, oubli d'un réseau enregistré.</summary>
public sealed class WifiService
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(10);

    private readonly WiFiAdapter? _adapter;

    public Radio? Radio { get; }
    public bool HasAdapter => _adapter is not null;
    public bool IsOn => Radio?.State == RadioState.On;

    private WifiService(WiFiAdapter? adapter, Radio? radio)
    {
        _adapter = adapter;
        Radio = radio;
    }

    public static async Task<WifiService> OpenAsync()
    {
        var radio = await Radios.FindAsync(RadioKind.WiFi);
        // Un appel qui ne répond pas ne doit pas figer l'écran : une nouvelle tentative suit à la prochaine recherche.
        var access = await WiFiAdapter.RequestAccessAsync().AsTask().WaitAsync(Answer);
        if (access != WiFiAccessStatus.Allowed)
        {
            Log.Info($"Wi-Fi : accès {access}, radio {(radio is null ? "absente" : radio.State.ToString())}");
            return new WifiService(null, radio);
        }
        var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(Answer);
        return new WifiService(adapters.FirstOrDefault(), radio);
    }

    /// <summary>Réseaux à portée, du connecté au plus faible ; un réseau diffusé par plusieurs bornes n'apparaît qu'une fois.</summary>
    public async Task<List<WifiNetwork>> ScanAsync()
    {
        if (_adapter is null) return [];
        await _adapter.ScanAsync();
        var connected = await ConnectedSsidAsync();
        var saved = NetworkInformation.GetConnectionProfiles().Where(p => p.IsWlanConnectionProfile).Select(p => p.ProfileName).ToHashSet();
        return _adapter.NetworkReport.AvailableNetworks
            .GroupBy(n => n.Ssid)
            .Select(g => g.MaxBy(n => n.SignalBars)!)
            .Select(n => new WifiNetwork(n.Ssid, n.SignalBars, SecurityOf(n), saved.Contains(n.Ssid), n.Ssid.Length > 0 && n.Ssid == connected, n))
            .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Saved).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid)
            .ToList();
    }

    private async Task<string?> ConnectedSsidAsync()
    {
        if (_adapter is null) return null;
        var profile = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
        return profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
    }

    private static WifiSecurity SecurityOf(WiFiAvailableNetwork n) => n.SecuritySettings.NetworkAuthenticationType.ToString() switch
    {
        "None" or "Open80211" or "Owe" => WifiSecurity.Open,
        "Wpa" or "Rsna" or "Wpa3" or "Wpa3Enterprise" or "Wpa3Enterprise192Bits" or "Ihv" => WifiSecurity.Enterprise,
        _ => WifiSecurity.Password,
    };

    /// <param name="password">Null : réseau ouvert, ou mot de passe déjà enregistré dans Windows.</param>
    /// <param name="hiddenSsid">Nom d'un réseau masqué.</param>
    public async Task<WiFiConnectionStatus> ConnectAsync(WifiNetwork network, string? password, string? hiddenSsid = null)
    {
        if (_adapter is null || network.Raw is null) return WiFiConnectionStatus.UnspecifiedFailure;
        var credential = password is null ? new PasswordCredential() : new PasswordCredential { Password = password };
        var result = hiddenSsid is null
            ? await _adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic, credential)
            : await _adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic, credential, hiddenSsid);
        return result.ConnectionStatus;
    }

    public void Disconnect() => _adapter?.Disconnect();

    /// <summary>Connexion établie mais limitée : portail d'hôtel ou de gare, à valider dans le navigateur.</summary>
    public static bool NeedsPortal()
        => NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.ConstrainedInternetAccess;

    /// <summary>Oublie un réseau enregistré (Windows n'expose cette action que par netsh).</summary>
    public static async Task<bool> ForgetAsync(string ssid)
    {
        try
        {
            var start = new ProcessStartInfo("netsh") { CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "wlan", "delete", "profile", $"name={ssid}" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Oubli d'un réseau Wi-Fi", ex);
            return false;
        }
    }
}
