using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Services;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;

namespace MaRogAlly.Views;

/// <summary>
/// Interfaces Wi-Fi et Bluetooth, au centre de l'écran : tout ce qu'il faut pour se passer des paramètres de Windows
/// au quotidien (activer, rechercher, se connecter, associer, oublier).
/// </summary>
/// <param name="radioChanged">Un interrupteur a pu changer : le menu principal remet à jour ses boutons.</param>
public sealed class NetworkMenus(Action<OverlayBase> open, Action<string, string> toast, Action radioChanged)
{
    private const string WifiIcon = "", BluetoothIcon = "", SettingsIcon = "", LockedIcon = "";

    private static SidePanel Centered(string title, string subtitle = "")
    {
        var panel = new SidePanel(title, subtitle);
        panel.AnchorCenter();
        // Fond opaque, cartes et cadre de sélection du menu principal.
        panel.UseMenuStyle();
        return panel;
    }

    private static void AddWindowsSettings(SidePanel panel, string page)
        => panel.AddApp("Paramètres avancés de Windows", null, () => GameLauncher.Open(page), SettingsIcon);

    /// <summary>Force du signal : quatre barres, celles qui manquent restent discrètes.</summary>
    private static FrameworkElement SignalBars(byte bars)
    {
        var icon = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 4; i++)
            icon.Children.Add(new Border
            {
                Width = 4, Height = 6 + i * 4, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.White, Opacity = i < bars ? 1 : 0.22,
            });
        return icon;
    }

    private static FrameworkElement Glyph(SidePanel panel, string glyph) => new TextBlock
    {
        Text = glyph, FontFamily = (FontFamily)panel.FindResource("Icons"), FontSize = 20, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------- Wi-Fi ----------------

    /// <param name="demo">Réseaux d'exemple, pour vérifier l'affichage sur une machine sans carte Wi-Fi (« --network demo »).</param>
    public void OpenWifi(bool demo = false)
    {
        var panel = Centered("Wi-Fi", "Recherche…");
        WifiService? wifi = null;
        List<WifiNetwork> networks = demo
            ? [new("Maison", 4, WifiSecurity.Password, true, true, null), new("Livebox-7A2C", 3, WifiSecurity.Password, false, false, null),
               new("Café du coin", 2, WifiSecurity.Open, false, false, null), new("Bureau", 2, WifiSecurity.Enterprise, false, false, null), new("", 1, WifiSecurity.Password, false, false, null)]
            : [];
        var loaded = demo;
        var scanFailed = false;
        var shown = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };

        void Build()
        {
            panel.Clear();
            if (!loaded) return;
            if (!demo && wifi?.HasAdapter != true)
            {
                // Interrupteur présent mais liste refusée : Windows demande l'autorisation de localisation.
                if (wifi?.Radio is { } radio)
                {
                    panel.SetSubtitle("Windows refuse l'accès aux réseaux Wi-Fi");
                    panel.AddToggle("Wi-Fi", radio.State == Windows.Devices.Radios.RadioState.On, Toggle, asSwitch: true);
                    panel.AddApp("Autoriser la localisation pour voir les réseaux", null, () => GameLauncher.Open("ms-settings:privacy-location"), "\uE81D");
                }
                else panel.SetSubtitle("Aucune carte Wi-Fi détectée");
                AddWindowsSettings(panel, "ms-settings:network-wifi");
                return;
            }
            var on = demo || wifi!.IsOn;
            var connected = networks.FirstOrDefault(n => n.Connected);
            panel.SetSubtitle(!on ? "Désactivé" : connected is not null ? $"Connecté à {connected.Ssid}" : "Non connecté");
            panel.AddToggle("Wi-Fi", on, Toggle, asSwitch: true);
            if (on)
            {
                panel.AddHeader(networks.Count > 0 ? "Réseaux à portée" : "Aucun réseau trouvé");
                foreach (var n in networks)
                {
                    var name = n.IsHidden ? "Réseau masqué" : n.Ssid;
                    var detail = n.Connected ? "Connecté" : n.Saved ? "Enregistré" : n.Security switch
                    {
                        WifiSecurity.Open => "Ouvert",
                        WifiSecurity.Enterprise => "Réseau d'entreprise",
                        _ => "Sécurisé",
                    };
                    panel.AddRow(SignalBars(n.Bars), name, detail, n.Connected, () => Choose(n), n.Ssid.Length > 0 ? n.Ssid : "hidden");
                }
                // Windows masque la liste aux applications sans l'autorisation de localisation.
                if (networks.Count == 0 || scanFailed)
                {
                    panel.AddHeader(scanFailed ? "Recherche refusée par Windows" : "");
                    panel.AddApp("Autoriser la localisation pour voir les réseaux", null, () => GameLauncher.Open("ms-settings:privacy-location"), "");
                }
            }
            panel.AddHeader("");
            AddWindowsSettings(panel, "ms-settings:network-wifi");
        }

        async void Refresh(bool force = false)
        {
            try
            {
                wifi ??= await WifiService.OpenAsync();
                if (wifi.IsOn)
                {
                    try
                    {
                        networks = await wifi.ScanAsync();
                        scanFailed = false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Recherche des réseaux Wi-Fi", ex);
                        scanFailed = true;
                    }
                }
                else networks = [];
            }
            catch (Exception ex)
            {
                Log.Error("Ouverture du Wi-Fi", ex);
            }
            loaded = true;
            // Liste inchangée : on n'y touche pas, la sélection et le cadre restent en place.
            var now = string.Join('|', networks.Select(n => $"{n.Ssid}{n.Bars}{n.Connected}{n.Saved}")) + scanFailed + wifi?.IsOn;
            if (force || now != shown)
            {
                shown = now;
                panel.Rebuild(Build);
            }
        }

        async void Toggle(bool on)
        {
            if (wifi?.Radio is { } radio && !await Radios.SetAsync(radio, on)) toast("", "Windows a refusé de changer l'état du Wi-Fi");
            radioChanged();
            // La carte met un instant à démarrer avant de pouvoir chercher.
            if (on) await Task.Delay(1500);
            Refresh(force: true);
        }

        void Choose(WifiNetwork n)
        {
            if (n.Raw is null) return;
            if (n.Connected)
            {
                Actions(n, ("Se déconnecter", () =>
                {
                    wifi?.Disconnect();
                    Refresh(force: true);
                }));
            }
            else if (n.Security == WifiSecurity.Enterprise)
            {
                toast(WifiIcon, "Réseau d'entreprise : à configurer dans les paramètres de Windows");
                GameLauncher.Open("ms-settings:network-wifi");
            }
            else if (n.IsHidden)
            {
                var name = new KeyboardPanel("", "Nom du réseau masqué", lowercase: true);
                name.Submitted += ssid =>
                {
                    if (ssid.Length > 0) AskPassword(n, ssid);
                };
                open(name);
            }
            else if (n.Saved) Actions(n, ("Se connecter", () => Connect(n, null, null)));
            else if (n.Security == WifiSecurity.Open) Connect(n, null, null);
            else AskPassword(n, null);
        }

        void Actions(WifiNetwork n, (string Label, Action Run) main)
        {
            var actions = Centered(n.Ssid);
            actions.AddChoice(main.Label, false, () =>
            {
                actions.Close();
                main.Run();
            });
            actions.AddChoice("Oublier ce réseau", false, async () =>
            {
                actions.Close();
                toast(WifiIcon, await WifiService.ForgetAsync(n.Ssid) ? $"{n.Ssid} oublié" : "Windows a refusé d'oublier ce réseau");
                Refresh(force: true);
            });
            actions.AddChoice("Annuler", false, actions.Close);
            open(actions);
        }

        void AskPassword(WifiNetwork n, string? hiddenSsid)
        {
            var keyboard = new KeyboardPanel("", $"Mot de passe de {hiddenSsid ?? n.Ssid}", lowercase: true);
            keyboard.Submitted += password =>
            {
                if (password.Length > 0) Connect(n, password, hiddenSsid);
            };
            open(keyboard);
        }

        async void Connect(WifiNetwork n, string? password, string? hiddenSsid)
        {
            var name = hiddenSsid ?? n.Ssid;
            panel.SetSubtitle($"Connexion à {name}…");
            WiFiConnectionStatus status;
            try
            {
                status = wifi is null ? WiFiConnectionStatus.UnspecifiedFailure : await wifi.ConnectAsync(n, password, hiddenSsid);
            }
            catch (Exception ex)
            {
                Log.Error("Connexion Wi-Fi", ex);
                status = WiFiConnectionStatus.UnspecifiedFailure;
            }
            switch (status)
            {
                case WiFiConnectionStatus.Success:
                    toast(WifiIcon, $"Connecté à {name}");
                    // Hôtel, gare… : la page de connexion du réseau s'ouvre dans le navigateur.
                    if (WifiService.NeedsPortal()) GameLauncher.Open("http://www.msftconnecttest.com/redirect");
                    break;
                case WiFiConnectionStatus.InvalidCredential:
                    toast(LockedIcon, "Mot de passe incorrect");
                    AskPassword(n, hiddenSsid);
                    break;
                case WiFiConnectionStatus.Timeout:
                    toast(WifiIcon, $"{name} ne répond pas");
                    break;
                case WiFiConnectionStatus.NetworkNotAvailable:
                    toast(WifiIcon, $"{name} n'est plus à portée");
                    break;
                default:
                    toast(WifiIcon, $"Connexion à {name} impossible");
                    break;
            }
            Refresh(force: true);
        }

        timer.Tick += (_, _) =>
        {
            // Pas de relecture sous le clavier ou un choix ouvert par-dessus.
            if (panel.IsKeyboardFocusWithin) Refresh();
        };
        panel.CloseRequested += _ =>
        {
            timer.Stop();
            radioChanged();
        };
        Build();
        open(panel);
        if (!demo)
        {
            Refresh();
            timer.Start();
        }
    }

    // ---------------- Bluetooth ----------------

    public void OpenBluetooth()
    {
        var panel = Centered("Bluetooth", "Recherche…");
        BluetoothService? bluetooth = null;
        List<BluetoothEntry> devices = [];
        var loaded = false;
        var shown = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void Build()
        {
            panel.Clear();
            if (!loaded) return;
            if (bluetooth?.Radio is null)
            {
                panel.SetSubtitle("Aucun Bluetooth détecté (ou accès refusé par Windows)");
                AddWindowsSettings(panel, "ms-settings:bluetooth");
                return;
            }
            var on = bluetooth.IsOn;
            var connected = devices.Where(d => d.Connected).ToList();
            panel.SetSubtitle(!on ? "Désactivé" : connected.Count > 0 ? $"Connecté à {string.Join(", ", connected.Select(d => d.Name))}" : "Aucun appareil connecté");
            panel.AddToggle("Bluetooth", on, Toggle, asSwitch: true);
            if (on)
            {
                var paired = devices.Where(d => d.Paired).ToList();
                if (paired.Count > 0)
                {
                    panel.AddHeader("Mes appareils");
                    foreach (var d in paired)
                        panel.AddRow(Glyph(panel, BluetoothIcon), d.Name, d.Connected ? "Connecté" : "Associé", d.Connected, () => Manage(d), d.Name);
                }
                var nearby = devices.Where(d => !d.Paired).ToList();
                panel.AddHeader(nearby.Count > 0 ? "Appareils à portée : A pour associer" : "Recherche d'appareils à portée…");
                foreach (var d in nearby)
                    panel.AddRow(Glyph(panel, BluetoothIcon), d.Name, null, false, () => Pair(d), d.Name);
            }
            panel.AddHeader("");
            AddWindowsSettings(panel, "ms-settings:bluetooth");
        }

        void Update(bool force = false)
        {
            devices = bluetooth?.IsOn == true ? bluetooth.Entries() : [];
            var now = string.Join('|', devices.Select(d => $"{d.Name}{d.Paired}{d.Connected}")) + bluetooth?.IsOn;
            if (force || now != shown)
            {
                shown = now;
                panel.Rebuild(Build);
            }
        }

        async void Toggle(bool on)
        {
            if (bluetooth?.Radio is { } radio && !await Radios.SetAsync(radio, on)) toast("", "Windows a refusé de changer l'état du Bluetooth");
            radioChanged();
            bluetooth?.Stop();
            if (on)
            {
                await Task.Delay(800);
                bluetooth?.Start();
            }
            Update(force: true);
        }

        void Manage(BluetoothEntry d)
        {
            var actions = Centered(d.Name, d.Connected ? "Connecté" : "Associé");
            if (!d.Connected)
                actions.AddChoice("Se connecter", false, async () =>
                {
                    actions.Close();
                    toast(BluetoothIcon, $"Connexion à {d.Name}…");
                    var done = bluetooth is not null && await bluetooth.ConnectAsync(d.Id);
                    toast(BluetoothIcon, done ? $"Connecté à {d.Name}" : $"{d.Name} ne s'est pas connecté : allume-le, ou passe par les paramètres de Windows");
                });
            actions.AddChoice("Dissocier cet appareil", false, async () =>
            {
                actions.Close();
                var done = bluetooth is not null && await bluetooth.UnpairAsync(d.Id);
                toast(BluetoothIcon, done ? $"{d.Name} dissocié" : $"Impossible de dissocier {d.Name}");
            });
            actions.AddChoice("Annuler", false, actions.Close);
            open(actions);
        }

        async void Pair(BluetoothEntry d)
        {
            toast(BluetoothIcon, $"Association avec {d.Name}…");
            SidePanel? code = null;
            bool done;
            try
            {
                done = bluetooth is not null && await bluetooth.PairAsync(d.Id, (kind, pin) =>
                    Application.Current.Dispatcher.InvokeAsync(() => AskPairing(d, kind, pin, shownCode => code = shownCode)).Task.Unwrap());
            }
            catch (Exception ex)
            {
                Log.Error("Association Bluetooth", ex);
                done = false;
            }
            code?.Close();
            toast(BluetoothIcon, done ? $"{d.Name} associé" : $"Association avec {d.Name} impossible");
        }

        Task<string?> AskPairing(BluetoothEntry d, DevicePairingKinds kind, string pin, Action<SidePanel> keepCode)
        {
            var answer = new TaskCompletionSource<string?>();
            switch (kind)
            {
                case DevicePairingKinds.DisplayPin:
                    // Code à saisir sur l'appareil : il reste affiché jusqu'à la fin de l'association.
                    var code = Centered(pin, $"Saisis ce code sur {d.Name}");
                    code.AddChoice("Fermer", false, code.Close);
                    keepCode(code);
                    open(code);
                    answer.SetResult("");
                    break;
                case DevicePairingKinds.ConfirmPinMatch:
                    var match = Centered(pin, $"Ce code est-il affiché sur {d.Name} ?");
                    match.AddChoice("Oui, associer", false, () =>
                    {
                        answer.TrySetResult("");
                        match.Close();
                    });
                    match.AddChoice("Non", false, match.Close);
                    match.CloseRequested += _ => answer.TrySetResult(null);
                    open(match);
                    break;
                case DevicePairingKinds.ProvidePin:
                    var keyboard = new KeyboardPanel("", $"Code de {d.Name}", lowercase: true);
                    keyboard.Submitted += value => answer.TrySetResult(value);
                    keyboard.CloseRequested += _ => answer.TrySetResult(null);
                    open(keyboard);
                    break;
                default:
                    answer.SetResult("");
                    break;
            }
            return answer.Task;
        }

        async void Load()
        {
            try
            {
                bluetooth = await BluetoothService.OpenAsync();
                if (bluetooth.IsOn) bluetooth.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Ouverture du Bluetooth", ex);
            }
            loaded = true;
            Update(force: true);
            timer.Start();
        }

        timer.Tick += (_, _) =>
        {
            if (panel.IsKeyboardFocusWithin) Update();
        };
        panel.CloseRequested += _ =>
        {
            timer.Stop();
            bluetooth?.Stop();
            radioChanged();
        };
        Build();
        open(panel);
        Load();
    }
}
