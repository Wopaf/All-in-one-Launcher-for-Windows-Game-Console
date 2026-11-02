using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Microsoft.Web.WebView2.Core;

namespace MaRogAlly.Views;

/// <summary>
/// Navigateur intégré en lecture seule : affiche une page web dont on ne garde qu'un élément
/// (ex. la liste des résultats de recherche d'Instant Gaming). On défile à la manette ;
/// « Retour » ferme, l'autre bouton ouvre la page dans le navigateur par défaut.
/// </summary>
public partial class WebPreview : OverlayBase
{
    private readonly string _url;
    private readonly string _keepSelector;

    /// <summary>
    /// Masque tout sauf l'élément gardé (et ses parents) ; relancé quand la page ajoute du contenu
    /// (bannière de cookies, chargement différé). Les clics sont désactivés.
    /// </summary>
    private const string IsolateScript = """
        (function (selector) {
            const style = document.createElement('style');
            style.textContent = 'html, body { background: transparent !important; overflow-x: hidden !important; }'
                + ' * { pointer-events: none !important; } ::-webkit-scrollbar { display: none; }';
            let first = true;
            function isolate() {
                const keep = document.querySelector(selector);
                if (!keep) return;
                if (!style.isConnected) document.head.appendChild(style);
                let el = keep;
                while (el && el.parentElement) {
                    const parent = el.parentElement;
                    for (const sibling of parent.children) {
                        if (sibling === el || ['SCRIPT', 'STYLE', 'LINK'].includes(sibling.tagName)) continue;
                        sibling.style.setProperty('display', 'none', 'important');
                    }
                    if (parent !== document.documentElement) {
                        for (const p of ['margin', 'padding', 'max-width', 'min-height', 'transform'])
                            parent.style.setProperty(p, p === 'max-width' ? 'none' : p === 'transform' ? 'none' : '0', 'important');
                        parent.style.setProperty('position', 'static', 'important');
                    }
                    el = parent;
                }
                keep.style.setProperty('margin', '0', 'important');
                // L'en-tête masqué laisse la page défilée : on repart du haut de la liste.
                if (first) { first = false; window.scrollTo(0, 0); }
            }
            isolate();
            let pending = false;
            new MutationObserver(() => {
                if (pending) return;
                pending = true;
                requestAnimationFrame(() => { pending = false; isolate(); });
            }).observe(document.documentElement, { childList: true, subtree: true });
        })(SELECTOR);
        """;

    public WebPreview(string siteName, string query, string url, string keepSelector)
    {
        InitializeComponent();
        _url = url;
        _keepSelector = keepSelector;
        SiteText.Text = siteName.ToUpperInvariant();
        QueryText.Text = $"« {query} »";
        OpenText.Text = $"Ouvrir sur {siteName}";
        Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        // Le navigateur ne garde jamais le focus : clavier et manette restent à l'application.
        Web.GotFocus += (_, _) => Dispatcher.BeginInvoke(() => FocusNav.Focus(BackButton));
        CloseRequested += _ => Web.Dispose();
    }

    protected override FrameworkElement AnimatedPart => Body;
    protected override double EnterFromX => 0;
    protected override FrameworkElement FocusScope => Actions;
    public override string Hints => "↕:Défiler|A:Valider|B:Retour";

    public override void OnOpened()
    {
        base.OnOpened();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "webview"));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            // Aucune autre page ne s'ouvre dans ce cadre (liens, fenêtres surgissantes).
            core.NewWindowRequested += (_, e) => e.Handled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('DOMContentLoaded', () => { " + IsolateScript.Replace("SELECTOR", System.Text.Json.JsonSerializer.Serialize(_keepSelector)) + " });");
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    LoadingIcon.Text = "";
                    Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                    LoadingText.Text = "Impossible de charger la page. Vérifie ta connexion, ou ouvre-la dans ton navigateur.";
                    return;
                }
                Loading.Visibility = Visibility.Collapsed;
                Web.Visibility = Visibility.Visible;
            };
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            Log.Error("Navigateur intégré", ex);
            LoadingIcon.Text = "";
            Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            LoadingText.Text = "Le moteur web de Windows (WebView2) n'est pas disponible. Ouvre la page dans ton navigateur.";
        }
    }

    private void Scroll(int delta)
    {
        if (Web.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync($"window.scrollBy({{ top: {delta}, behavior: 'smooth' }})");
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        GameLauncher.Open(_url);
        Close();
    }

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up:
                Scroll(-320);
                return true;
            case NavAction.Down:
                Scroll(320);
                return true;
        }
        return base.Handle(action);
    }
}
