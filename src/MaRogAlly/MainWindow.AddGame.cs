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
        int? appId = null;
        List<(int AppId, string Name)> found = [];
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
                var result = await _discover.SuggestAsync(term, cts.Token);
                if (cts.IsCancellationRequested) return;
                found = result;
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

        void Choose((int AppId, string Name) hit)
        {
            name = hit.Name;
            appId = hit.AppId;
            image = SteamStore.CoverUrls(hit.AppId).First();
            found = [];
            panel.Rebuild(Build);
        }

        void ChooseFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Fichier qui lance le jeu",
                Filter = "Programmes et raccourcis|*.exe;*.lnk;*.url;*.bat;*.cmd|Tous les fichiers|*.*",
            };
            if (dialog.ShowDialog(this) != true) return;
            file = dialog.FileName;
            // Pas encore de nom : celui du programme, et la recherche part de là.
            if (name.Length == 0)
            {
                name = FileTitle(file);
                Search(name);
            }
            panel.Rebuild(Build);
        }

        void EditImage()
        {
            var keyboard = new KeyboardPanel(image, "Adresse de l'image (https://…)", lowercase: true);
            keyboard.Submitted += v =>
            {
                image = v.Trim();
                panel.Rebuild(Build);
            };
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
                Search(v);
            };
            // Clavier fermé : la sélection va au premier jeu trouvé, sinon au fichier.
            keyboard.CloseRequested += _ => Dispatcher.BeginInvoke(() =>
            {
                if (found.Count > 0) panel.FocusTag($"hit:{found[0].AppId}");
                else panel.FocusTag("file");
            }, DispatcherPriority.Input);
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
                : appId is not null ? "Jeu reconnu : son image et sa fiche seront ajoutées"
                : "Nom choisi (A pour le modifier)", appId is not null, EditName, tag: "name");

            if (searching && found.Count == 0) panel.AddHeader("Recherche…");
            else if (found.Count > 0 && appId is null)
            {
                panel.AddHeader("Jeux trouvés");
                foreach (var hit in found)
                    panel.AddRow(Thumb(SteamStore.CoverUrls(hit.AppId).Last()), hit.Name, "Choisir : nom et image remplis", false, () => Choose(hit), tag: $"hit:{hit.AppId}");
            }

            panel.AddHeader("Jeu");
            panel.AddRow(Glyph(""), "Fichier à lancer", file.Length == 0 ? "Choisir le programme ou le raccourci du jeu" : file, file.Length > 0, ChooseFile, tag: "file");
            panel.AddRow(image.Length > 0 ? Thumb(image) : Glyph(""), "Image du jeu",
                image.Length == 0 ? "Adresse (URL) de la jaquette, facultative" : image, false, EditImage, tag: "image");

            panel.AddHeader("");
            panel.AddChoice("Ajouter à Mes Jeux", false, Add);
            panel.AddChoice("Annuler", false, panel.Close);
        }

        Build();
        OpenOverlay(panel);
        if (initialName is { Length: > 0 })
        {
            name = initialName;
            Search(name);
        }
    }

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
