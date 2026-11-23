using Windows.Networking.Connectivity;
using MaRogAlly.Core;

namespace MaRogAlly.Services;

/// <summary>Accès à internet, tel que Windows le voit (icône réseau de la barre des tâches).</summary>
public static class Connectivity
{
    /// <summary>
    /// Vrai si Windows a accès à internet. Un Wi-Fi connecté sans internet (portail d'hôtel, box en panne) compte comme
    /// hors ligne. En cas de doute (Windows ne répond pas), on suppose la connexion présente pour ne rien bloquer à tort.
    /// </summary>
    public static bool IsOnline
    {
        get
        {
            try
            {
                return NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch (Exception ex)
            {
                Log.Error("État de la connexion internet", ex);
                return true;
            }
        }
    }
}
