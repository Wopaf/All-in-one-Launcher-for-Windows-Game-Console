using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaRogAlly.Core;
using MaRogAlly.Metadata;
using MaRogAlly.Models;
using MaRogAlly.UI;
using MaRogAlly.Views;

namespace MaRogAlly;

/// <summary>Ajout d'un jeu à la main (fichier à lancer, nom, image), avec recherche du nom parmi les jeux connus.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Formulaire « Ajouter un jeu » : en haut, le nom sert de recherche (jeux du magasin Steam) ; choisir un résultat
    /// remplit le nom et l'image. Puis le fichier à lancer, l'adresse de l'image, et le bouton d'ajout.
    /// </summary>
    private void OpenAddGame(string? initialName = null)
    {
        var panel = new SidePanel("Ajouter un jeu");
        panel.AnchorLeft(bottom: false);
        panel.Float();
        panel.UseMenuStyle();
        panel.Widen(600);
        panel.SetHints("A:Valider|B:Fermer");

        string name = "", file = "", image = "";
        string? hero = null;
        GameSuggestion? chosen = null;
        int? appId = null;
        List<GameSuggestion> found = [];
        var searching = false;
        CancellationTokenSource? searchCts = null;

        // Recherche après une courte pause dans la saisie : pas une requête par lettre tapée.
        async void Search(string term)
        {
            searchCts?.Cancel();
            var cts = searchCts = new CancellationTokenSource();
            term = term.Trim();
            if (term.Length < 2)
            {
                found = [];
                searching = false;
                Build();
                return;
            }
            searching = true;
            Build();
            try
            {
                await Task.Delay(350, cts.Token);
                // Tout le catalogue IGDB (consoles actuelles et rétro, PC), puis les jeux du magasin Steam qu'il n'a pas.
                var igdb = _metadata.Igdb.SearchAsync(term, cts.Token);
                var steam = _discover.SuggestAsync(term, cts.Token);
                var all = (await igdb).ToList();
                var known = all.Select(g => Text.Normalize(g.Name)).ToHashSet();
                all.AddRange((await steam).Where(g => !known.Contains(Text.Normalize(g.Name))));
                if (cts.IsCancellationRequested) return;
                found = all.Take(12).ToList();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("Recherche du nom d'un jeu", ex);
                found = [];
            }
            searching = false;
            Build();
        }

        void Choose(GameSuggestion hit)
        {
            name = hit.Name;
            appId = hit.SteamAppId;
            chosen = hit;
            image = hit.CoverUrl ?? "";
            hero = hit.HeroUrl;
            found = [];
            panel.Rebuild(Build);
            Refocus("file");
        }

        // Retour dans le formulaire (après le choix du fichier ou le clavier) : la sélection reste sur la ligne d'où l'on vient,
        // une fois que tout le reste (fermeture du clavier, reconstruction) est passé.
        void Refocus(string tag) => Dispatcher.BeginInvoke(() => panel.FocusTag(tag), DispatcherPriority.ApplicationIdle);

        void ChooseFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Fichier qui lance le jeu",
                Filter = "Programmes et raccourcis|*.exe;*.lnk;*.url;*.bat;*.cmd|Tous les fichiers|*.*",
            };
            // Fenêtre de Windows : la manette y pilote la souris (stick droit, RB pour cliquer, B pour fermer).
            bool? picked;
            SetFilePicking(true);
            try { picked = dialog.ShowDialog(this); }
            finally { SetFilePicking(false); }
            if (picked == true)
            {
                file = dialog.FileName;
                // Pas encore de nom : celui du programme, et la recherche part de là.
                if (name.Length == 0)
                {
                    name = FileTitle(file);
                    Search(name);
                }
                panel.Rebuild(Build);
            }
            Refocus("file");
        }

        void EditImage()
        {
            var keyboard = new KeyboardPanel(image, "Adresse de l'image (https://…)", lowercase: true);
            keyboard.Submitted += v =>
            {
                image = v.Trim();
                panel.Rebuild(Build);
            };
            keyboard.CloseRequested += _ => Refocus("image");
            OpenOverlay(keyboard);
        }

        void EditName()
        {
            var keyboard = new KeyboardPanel(name, "Nom du jeu…");
            keyboard.ValueChanged += v =>
            {
                // Nom changé à la main : il n'est plus celui du jeu choisi.
                name = v;
                appId = null;
                chosen = null;
                hero = null;
                Search(v);
            };
            // Clavier fermé : la sélection va au premier jeu trouvé, sinon au fichier.
            keyboard.CloseRequested += _ => Refocus(found.Count > 0 ? "hit:0" : "file");
            OpenOverlay(keyboard);
        }

        void Add()
        {
            if (file.Length == 0 || !File.Exists(file))
            {
                Toast("", "Choisis d'abord le fichier qui lance le jeu");
                panel.FocusTag("file");
                return;
            }
            var title = name.Trim().Length > 0 ? name.Trim() : FileTitle(file);
            // Même fichier déjà ajouté : sa fiche est mise à jour plutôt que doublée.
            var entry = _settings.ManualGames.FirstOrDefault(m => string.Equals(m.Exe, file, StringComparison.OrdinalIgnoreCase));
            if (entry is null) _settings.ManualGames.Add(entry = new ManualGame { Exe = file });
            entry.Title = title;
            entry.CoverUrl = image.Length > 0 ? image : null;
            entry.HeroUrl = hero;
            entry.SteamAppId = appId;
            _settings.Save();
            Log.Info($"Jeu ajouté à la main : {title}");
            panel.Close();
            Toast("", $"{title} est ajouté à Mes Jeux");
            _ = AddedGameScanAsync();
        }

        void Build()
        {
            panel.Clear();
            panel.AddRow(Glyph(""), name.Length == 0 ? "Rechercher le jeu" : name,
                name.Length == 0 ? "Tape son nom : les jeux connus s'affichent dessous"
                : chosen is not null ? $"Jeu reconnu ({chosen.Detail}) : son image et sa fiche seront ajoutées"
                : "Nom choisi (A pour le modifier)", chosen is not null, EditName, tag: "name");

            if (searching && found.Count == 0) panel.AddHeader("Recherche…");
            else if (found.Count > 0 && chosen is null)
            {
                panel.AddHeader("Jeux trouvés");
                for (var i = 0; i < found.Count; i++)
                {
                    var hit = found[i];
                    panel.AddRow(Thumb(hit.CoverUrl ?? ""), hit.Name, hit.Detail.Length > 0 ? hit.Detail : "Choisir : nom et image remplis", false, () => Choose(hit), tag: $"hit:{i}");
                }
            }
            else if (!searching && found.Count == 0 && chosen is null && name.Trim().Length >= 2 && !_metadata.Igdb.IsConfigured)
                panel.AddHeader("Jeux PC seulement : ajoute les clés IGDB (Réglages) pour chercher sur toutes les consoles");

            panel.AddHeader("Jeu");
            panel.AddRow(Glyph(""), "Fichier à lancer", file.Length == 0 ? "Choisir le programme ou le raccourci du jeu" : file, file.Length > 0, ChooseFile, tag: "file");
            panel.AddRow(image.Length > 0 ? Thumb(image) : Glyph(""), "Image du jeu",
                image.Length == 0 ? "Adresse (URL) de la jaquette, facultative" : image, false, EditImage, tag: "image");

            // Annuler et Ajouter côte à côte, en bas du formulaire.
            panel.AddHeader("");
            panel.AddAppGrid(2,
            [
                ("Annuler", null, panel.Close, ""),
                ("Ajouter à Mes Jeux", null, Add, ""),
            ]);
        }

        Build();
        OpenOverlay(panel);
        if (initialName is { Length: > 0 })
        {
            name = initialName;
            Search(name);
        }
    }

    private bool _filePicking;

    /// <summary>
    /// Fenêtre de choix d'un fichier ouverte : la souris à la manette s'active (quel que soit le mode réglé), et la manette
    /// ne pilote plus l'application derrière. À la fermeture, tout revient comme avant.
    /// </summary>
    private void SetFilePicking(bool on)
    {
        _filePicking = on;
        _padMouseChecked = DateTime.MinValue; // pris en compte tout de suite, sans attendre la prochaine vérification
        if (!on) _padMouse.Release();
    }

    /// <summary>Touche Échap envoyée à la fenêtre au premier plan (ferme la fenêtre de choix d'un fichier).</summary>
    private static void PressEscape()
    {
        const byte escape = 0x1B;
        const uint keyUp = 0x0002;
        keybd_event(escape, 0, 0, UIntPtr.Zero);
        keybd_event(escape, 0, keyUp, UIntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    /// <summary>Après un ajout : le jeu apparaît tout de suite (disque), puis sa fiche et ses images arrivent (scan complet).</summary>
    private async Task AddedGameScanAsync()
    {
        await ScanAsync(initial: false, localOnly: true);
        await ScanAsync(initial: false);
    }

    /// <summary>Jeu ajouté à la main : retiré de Mes Jeux (le fichier du jeu n'est pas touché).</summary>
    private void RemoveManualGame(Game g)
    {
        var confirm = new SidePanel("Retirer de Mes Jeux ?", g.DisplayTitle);
        confirm.SetHints("A:Valider|B:Annuler");
        confirm.AddChoice("Oui, retirer", false, () =>
        {
            confirm.Close();
            _settings.ManualGames.RemoveAll(m => string.Equals(m.Exe, g.LaunchExe, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            CloseDetailOf(g);
            Toast("", $"{g.DisplayTitle} est retiré de Mes Jeux");
            _ = ScanAsync(initial: false, localOnly: true);
        }, "Le jeu reste sur le disque, seule sa vignette disparaît");
        confirm.AddChoice("Annuler", false, confirm.Close);
        OpenOverlay(confirm);
    }

    /// <summary>Referme la fiche de ce jeu si elle est ouverte (il vient de quitter la bibliothèque).</summary>
    private void CloseDetailOf(Game g)
    {
        foreach (var detail in _overlays.OfType<DetailView>().Where(d => d.Game == g).ToList()) detail.Close();
    }

    /// <summary>Nom lisible d'un programme : sa description (« Hollow Knight »), sinon le nom du fichier.</summary>
    private static string FileTitle(string path)
    {
        try
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && System.Diagnostics.FileVersionInfo.GetVersionInfo(path) is { } info)
            {
                if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private TextBlock Glyph(string glyph) => new()
    {
        Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>Petite jaquette (adresse web ou fichier) au début d'une ligne ; vide si l'image ne se charge pas.</summary>
    private static FrameworkElement Thumb(string source)
    {
        var box = new Border { Width = 28, Height = 40, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2A)), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        Rounded.SetRadius(box, 4);
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https" || uri.IsFile))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.DecodePixelWidth = 60;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                box.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
        }
        catch { }
        return box;
    }
}
