using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.Scanners;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;
    private string? _currentLink;
    private ButtonBase? _lastFocused;

    /// <summary>Saisie d'un texte : titre, valeur actuelle, application de la nouvelle valeur.</summary>
    public event Action<string, string, Action<string>>? EditRequested;
    public event Action? Changed, KeysChanged, RescanRequested, RefreshMetadataRequested, MissingMetadataRequested;
    public event Action? HiddenGamesRequested, AccentRequested, UiScaleRequested, ThemeRequested, AppUpdateRequested;
    private Border? _themeSwatch;
    private TextBlock? _themeName;
    private TextBlock? _uiScaleValue;

    private Border? _accentSwatch;
    private TextBlock? _accentName;

    // Liens des tutoriels (écrits « [texte](url) » dans les aides).
    private const string TwitchConsole = "https://dev.twitch.tv/console/apps/create";
    private const string IgdbDocs = "https://api-docs.igdb.com/#account-creation";
    private const string SteamGridDbApi = "https://www.steamgriddb.com/profile/preferences/api";
    private const string SteamApiKey = "https://steamcommunity.com/dev/apikey";
    private const string SteamPrivacy = "https://steamcommunity.com/my/edit/settings";
    private const string SteamIdFinder = "https://steamid.io/";

    public SettingsView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _menuHighlight = new SlideHighlight(MenuHighlight, MenuHost);
        _itemsHighlight = new SlideHighlight(ItemsHighlight, ItemsHost);
        // Même taille de titre que l'onglet Plateformes.
        SizeChanged += (_, _) => MenuTitle.FontSize = CategoryTitle.FontSize = ActualHeight < 760 ? 28 : 34;
        Build();
    }

    private readonly SlideHighlight _menuHighlight, _itemsHighlight;

    private void Build()
    {
        Items.Children.Clear();
        Menu.Children.Clear();
        _categories.Clear();

        Category("Bibliothèque", "\uE8F1", "Scan des jeux et fiches",
            "Relance la détection des jeux, complète les fiches et les visuels, et donne accès aux jeux masqués.");
        ActionRow("Actualiser les jeux", "", () => RescanRequested?.Invoke(),
            "Actualiser les jeux", "Relance le scan de toutes les plateformes pour trouver les jeux installés ou désinstallés.\n\nLe scan a aussi lieu automatiquement à chaque démarrage et quand tu reviens sur l'application.");
        ActionRow("Actualiser les fiches", "", () => MissingMetadataRequested?.Invoke(),
            "Actualiser les fiches", "Relance la recherche des fiches et des visuels uniquement pour les jeux auxquels il manque une image. Les fiches complètes ne sont pas touchées.\n\nCette vérification a aussi lieu à chaque démarrage.");
        ActionRow("Retélécharger toutes les fiches", "", () => RefreshMetadataRequested?.Invoke(),
            "Retélécharger toutes les fiches", "Efface le cache des fiches et des visuels, puis les télécharge à nouveau. Utile après avoir ajouté une clé SteamGridDB ou IGDB.");
        ActionRow("Afficher les jeux masqués", "", () => HiddenGamesRequested?.Invoke(),
            "Jeux masqués", "Liste des jeux que tu as masqués depuis leur fiche. Sélectionne un jeu pour le rendre de nouveau visible.");
        ActionRow("Ouvrir le dossier des données", "", () => GameLauncher.Open(AppPaths.Root),
            "Dossier des données", $"{AppPaths.Root}\n\nOn y trouve settings.json (pour ajouter des jeux à la main), le cache et le journal log.txt.");

        Category("Apparence", "\uE790", "Couleur d'accent et taille",
            "Personnalise la couleur des sélections et adapte la taille de l'interface à ton écran.");
        ThemeRow();
        AccentRow();
        UiScaleRow();

        Category("Au démarrage", "\uE7E8", "Application et lanceurs",
            "Ce qui s'ouvre au démarrage de la machine et avec Ma ROG Ally.");
        _startupCategory = _categories[^1];
        Switch("Ouvrir au démarrage", () => _settings.StartWithWindows, v => { _settings.StartWithWindows = v; AutoStart.Apply(v); },
            "Ouvrir au démarrage", "Ma ROG Ally s'ouvre automatiquement au démarrage de la machine, dès l'ouverture de ta session Windows, puis rescanne toutes les plateformes.");
        Header("Lanceurs à ouvrir avec l'application");

        Category("Comportement", "\uE713", "Sons, lancement d'un jeu et mises à jour",
            "Sons de l'interface, comportement au lancement d'un jeu et mises à jour de l'application.");
        Switch("Sons de l'interface", () => _settings.UiSounds, v => { _settings.UiSounds = v; Services.UiSounds.Enabled = v; },
            "Sons de l'interface", "Un son léger quand tu te déplaces, valides ou reviens en arrière, un autre à l'ouverture de l'application et quand une recherche aboutit.");
        Switch("Réduire au lancement d'un jeu", () => _settings.MinimizeOnLaunch, v => _settings.MinimizeOnLaunch = v,
            "Réduire au lancement", "Réduit l'application dans la barre des tâches dès qu'un jeu est lancé.");
        Switch("Réduire les autres applications avec B", () => _settings.MinimizeWithB, v => _settings.MinimizeWithB = v,
            "Réduire avec B", "Le bouton B de la manette réduit l'application au premier plan, si ce n'est pas Ma ROG Ally. Il reste sans effet dans un jeu de ta bibliothèque et dans toute fenêtre en plein écran.\n\nDésactive cette option si B réduit par erreur un jeu en fenêtre qui n'est pas dans ta bibliothèque.");
        Switch("Masquer la barre des tâches de Windows", () => _settings.HideWindowsTaskbar, v => { _settings.HideWindowsTaskbar = v; WindowsTaskbar.Apply(_settings); },
            "Barre des tâches de Windows", "Active le masquage automatique de Windows tant que l'application tourne : la barre ne repasse plus devant les autres applications, qui gagnent aussi sa place à l'écran. Elle reste accessible en glissant depuis le bas de l'écran. Ton réglage d'origine revient quand tu quittes l'application.");

        Header("Mises à jour de l'application");
        Switch("Chercher les nouvelles versions", () => _settings.CheckAppUpdates, v => _settings.CheckAppUpdates = v,
            "Nouvelles versions", $"Version installée : {AppUpdater.CurrentText}.\n\nL'application regarde régulièrement sur GitHub si une nouvelle version existe, et l'annonce dans le menu principal. Rien ne s'installe sans que tu le valides.");
        ActionRow("Vérifier maintenant", "", () => AppUpdateRequested?.Invoke(),
            "Vérifier maintenant", $"Version installée : {AppUpdater.CurrentText}.\n\nSi une nouvelle version existe, tu pourras l'installer : l'application se télécharge puis redémarre toute seule.");

        Category("Clés d'API", "\uE8D7", "IGDB, SteamGridDB et Steam",
            "Facultatives : elles enrichissent les fiches et les visuels, et donnent accès à toute ta bibliothèque Steam.");
        Header("Fiches de jeux (IGDB)");
        Field("Client ID IGDB", () => _settings.IgdbClientId, v => _settings.IgdbClientId = v, secret: false,
            "IGDB, l'équivalent de TMDB pour le jeu vidéo",
            "Titres, descriptions, notes, genres, jaquettes et artworks.\n\n" +
            $"1. Ouvre la [console développeur Twitch]({TwitchConsole}) (compte Twitch gratuit, double authentification activée).\n" +
            "2. Enregistre une application : URL de redirection http://localhost, catégorie « Application Integration », type « Confidentiel ».\n" +
            "3. Copie le Client ID et génère un Client Secret.\n\n" +
            $"Détails dans la [documentation IGDB]({IgdbDocs}).");
        Field("Client Secret IGDB", () => _settings.IgdbClientSecret, v => _settings.IgdbClientSecret = v, secret: true,
            "Client Secret IGDB",
            $"Généré sur la page de ton application dans la [console Twitch]({TwitchConsole}) (bouton « Nouveau secret »).");

        Header("Visuels (SteamGridDB)");
        Field("Clé API SteamGridDB", () => _settings.SteamGridDbKey, v => _settings.SteamGridDbKey = v, secret: true,
            "SteamGridDB",
            "Les plus beaux visuels : jaquettes, fonds d'écran et logos détourés.\n\n" +
            $"Connecte-toi puis ouvre la [page API de SteamGridDB]({SteamGridDbApi}) et clique sur « Generate API Key ».");

        Header("Steam (jeux non installés)");
        Field("Clé Web API Steam", () => _settings.SteamApiKey, v => _settings.SteamApiKey = v, secret: true,
            "Clé Web API Steam",
            "Permet d'afficher toute ta bibliothèque Steam, y compris les jeux non installés, avec ton temps de jeu.\n\n" +
            $"Obtiens-la sur la [page des clés Steam]({SteamApiKey}) (nom de domaine : localhost).\n\n" +
            $"Dans tes [paramètres de confidentialité Steam]({SteamPrivacy}), « Détails du jeu » doit être public.");
        Field("SteamID64", () => _settings.SteamId, v => _settings.SteamId = v, secret: false,
            "SteamID64",
            $"Laisse vide pour utiliser le compte connecté sur cette machine{(SteamScanner.DetectSteamId() is { } id ? $" ({id})" : "")}.\n\n" +
            $"Pour un autre compte, retrouve son identifiant sur [steamid.io]({SteamIdFinder}).");

        MenuSummary.Text = $"{_categories.Count} catégories";
        ShowCategory(Math.Clamp(_category, 0, _categories.Count - 1));
    }

    // ---------------- Catégories (menu de gauche) ----------------

    private sealed class SettingsCategory
    {
        public required string Name { get; init; }
        public required string Summary { get; init; }
        public required string Help { get; init; }
        public required StackPanel Panel { get; init; }
        public required Button MenuButton { get; init; }
        public ButtonBase? LastFocused { get; set; }
    }

    private readonly List<SettingsCategory> _categories = [];
    private int _category;
    private SettingsCategory Current => _categories[_category];
    /// <summary>Catégorie en cours de construction : les lignes suivantes y sont ajoutées.</summary>
    private StackPanel? _building;
    private SettingsCategory? _buildingCategory;

    private void Category(string name, string icon, string summary, string help)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 19, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var button = new Button { Content = grid, Style = (Style)FindResource("RowButton") };
        RowOptions.SetNoBar(button, true);
        RowOptions.SetFloating(button, true);
        var panel = new StackPanel();
        var category = new SettingsCategory { Name = name, Summary = summary, Help = help, Panel = panel, MenuButton = button };
        var index = _categories.Count;
        // Sélectionner une catégorie l'affiche à droite ; A ou → y entre.
        button.GotKeyboardFocus += (_, _) =>
        {
            _itemsHighlight.Hide();
            _menuHighlight.MoveTo(button);
            ShowCategory(index);
            ShowHelp(name, help + "\n\nAppuie sur A ou → pour modifier ces réglages.");
        };
        button.Click += (_, _) => EnterCategory();
        Menu.Children.Add(button);
        _categories.Add(category);
        _building = panel;
        _buildingCategory = category;
    }

    private void ShowCategory(int index)
    {
        if (_categories.Count == 0) return;
        var changed = index != _category || Items.Children.Count == 0;
        _category = index;
        if (!changed) return;
        if (!Menu.IsKeyboardFocusWithin) _menuHighlight.MoveTo(Current.MenuButton, 0.5);
        Items.Children.Clear();
        Items.Children.Add(Current.Panel);
        CategoryTitle.Text = Current.Name;
        CategorySummary.Text = Current.Summary;
        Scroll.ScrollToTop();
        Anim.Enter(Items, 24, 0, 260);
    }

    /// <summary>Passe du menu aux réglages de la catégorie (dernière ligne utilisée, sinon la première).</summary>
    private void EnterCategory()
    {
        var target = Current.LastFocused ?? Current.Panel.Children.OfType<ButtonBase>().FirstOrDefault();
        if (target is not null) FocusNav.Focus(target);
    }

    // ---------------- Constructeurs de lignes ----------------

    private void Header(string text)
        => _building!.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(18, _building.Children.Count == 0 ? 0 : 26, 0, 6),
        });

    private void Field(string label, Func<string> get, Action<string> set, bool secret, string helpTitle, string help)
    {
        var value = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        void Refresh()
        {
            var v = get();
            value.Text = string.IsNullOrWhiteSpace(v) ? "Non renseigné" : secret ? Mask(v) : v;
            value.Foreground = (Brush)FindResource(string.IsNullOrWhiteSpace(v) ? "TextMuted" : "TextSecondary");
        }
        Refresh();

        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(value);
        var b = Row(new Button { Content = grid }, helpTitle, help);
        b.Click += (_, _) => EditRequested?.Invoke(label, get(), v =>
        {
            set(v.Trim());
            Refresh();
            Changed?.Invoke();
            KeysChanged?.Invoke();
        });
    }

    private void Switch(object label, Func<bool> get, Action<bool> set, string helpTitle, string help)
    {
        var t = Row(new ToggleButton { Content = label, IsChecked = get(), Style = (Style)FindResource("SwitchRow") }, helpTitle, help);
        t.Click += (_, _) =>
        {
            set(t.IsChecked == true);
            Changed?.Invoke();
        };
    }

    private SettingsCategory? _startupCategory;
    private bool _launchersShown;

    /// <summary>Un interrupteur par lanceur installé, dans « Au démarrage » (connus seulement après la détection).</summary>
    public void SetLaunchers(IReadOnlyList<PlatformInfo> installed)
    {
        if (_launchersShown || _startupCategory is not { } category) return;
        _launchersShown = true;
        _building = category.Panel;
        _buildingCategory = category;
        foreach (var info in installed)
        {
            var id = info.Id;
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(info.Logo), Fill = Brushes.White, Stretch = Stretch.Uniform, Width = 18, Height = 18,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            });
            label.Children.Add(new TextBlock { Text = info.Name, VerticalAlignment = VerticalAlignment.Center });
            Switch(label, () => _settings.StartupLaunchers.Contains(id), v =>
            {
                if (v) _settings.StartupLaunchers.Add(id);
                else _settings.StartupLaunchers.Remove(id);
            }, info.Name, $"{info.Name} s'ouvre en arrière-plan avec Ma ROG Ally, s'il ne tourne pas déjà : tes jeux démarrent ensuite plus vite.\n\n" +
               (id is Platform.Steam or Platform.Epic
                   ? "Sa fenêtre est réduite pendant le chargement, puis fermée une fois le lanceur prêt (il reste actif dans la zone de notification)."
                   : "Sa fenêtre reste réduite dans la barre des tâches.") +
               "\n\nPris en compte à la prochaine ouverture de l'application.");
        }
    }

    /// <summary>Affiche une catégorie à l'ouverture (« --settings », pour vérifier l'affichage).</summary>
    public void PreviewCategory(string name) => ShowCategory(Math.Max(0, _categories.FindIndex(c => c.Name == name)));

    private void ActionRow(string label, string icon, Action run, string helpTitle, string help)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var b = Row(new Button { Content = sp }, helpTitle, help);
        b.Click += (_, _) => run();
    }

    private void ThemeRow()
    {
        _themeSwatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _themeName = new TextBlock { FontSize = 15, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_themeSwatch);
        right.Children.Add(_themeName);

        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = "Thème", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(right);
        var b = Row(new Button { Content = grid }, "Thème",
            "L'ambiance de l'application : couleur du fond, des panneaux et lueurs colorées. Aperçu en direct en parcourant la liste.");
        b.Click += (_, _) => ThemeRequested?.Invoke();
        UpdateTheme();
    }

    /// <summary>Met à jour la pastille et le nom du thème actuel.</summary>
    public void UpdateTheme()
    {
        if (_themeSwatch is null || _themeName is null) return;
        var theme = AppTheme.Find(_settings.Theme);
        _themeSwatch.Background = new SolidColorBrush(AppTheme.Swatch(theme));
        _themeName.Text = theme.Name;
    }

    private void AccentRow()
    {
        _accentSwatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _accentName = new TextBlock { FontSize = 15, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_accentSwatch);
        right.Children.Add(_accentName);

        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = "Couleur d'accent", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(right);
        var b = Row(new Button { Content = grid }, "Couleur d'accent",
            "La couleur des sélections, boutons, onglets et halos. Le changement s'applique immédiatement à toute l'application.");
        b.Click += (_, _) => AccentRequested?.Invoke();
        UpdateAccent();
    }

    private void UiScaleRow()
    {
        _uiScaleValue = new TextBlock { FontSize = 15, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = "Taille de l'interface", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(_uiScaleValue);
        var b = Row(new Button { Content = grid }, "Taille de l'interface",
            "Agrandit ou réduit toute l'application. En automatique, elle est réduite sur un petit écran comme celui de la ROG Ally (1920×1080 à 150 %).");
        b.Click += (_, _) => UiScaleRequested?.Invoke();
    }

    public void UpdateUiScale(string label)
    {
        if (_uiScaleValue is not null) _uiScaleValue.Text = label;
    }

    /// <summary>Met à jour la pastille et le nom de la couleur d'accent actuelle.</summary>
    public void UpdateAccent()
    {
        if (_accentSwatch is null || _accentName is null) return;
        _accentSwatch.Background = new SolidColorBrush(AccentTheme.Parse(_settings.AccentColor));
        _accentName.Text = AccentTheme.Choices.FirstOrDefault(c => string.Equals(c.Hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase)).Name
                           ?? _settings.AccentColor;
    }

    private T Row<T>(T control, string helpTitle, string help) where T : ButtonBase
    {
        if (control.Style is null) control.Style = (Style)FindResource("RowButton");
        RowOptions.SetNoBar(control, true);
        RowOptions.SetFloating(control, true);
        var category = _buildingCategory!;
        control.GotKeyboardFocus += (_, _) =>
        {
            _lastFocused = control;
            category.LastFocused = control;
            // Dans les réglages : le cadre glisse sur la ligne, la catégorie reste marquée (atténuée).
            _itemsHighlight.MoveTo(control);
            _menuHighlight.Dim(0.5);
            ShowHelp(helpTitle, help);
        };
        _building!.Children.Add(control);
        return control;
    }

    private static string Mask(string v) => v.Length <= 6 ? "••••••" : $"{v[..3]}••••••{v[^3..]}";

    // ---------------- Aide avec liens ----------------

    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\((https?://[^)]+)\)");

    /// <summary>Affiche l'aide ; les « [texte](url) » deviennent des liens cliquables.</summary>
    private void ShowHelp(string title, string help)
    {
        HelpTitle.Text = title;
        HelpText.Inlines.Clear();
        _currentLink = null;

        var last = 0;
        foreach (Match m in LinkRegex.Matches(help))
        {
            HelpText.Inlines.Add(new Run(help[last..m.Index]));
            var url = m.Groups[2].Value;
            _currentLink ??= url;
            var link = new Hyperlink(new Run(m.Groups[1].Value))
            {
                NavigateUri = new Uri(url),
                Foreground = (Brush)FindResource("Accent"),
                Cursor = Cursors.Hand,
            };
            link.RequestNavigate += (_, e) =>
            {
                GameLauncher.Open(e.Uri.AbsoluteUri);
                e.Handled = true;
            };
            HelpText.Inlines.Add(link);
            last = m.Index + m.Length;
        }
        HelpText.Inlines.Add(new Run(help[last..]));

        LinkHint.Visibility = _currentLink is null ? Visibility.Collapsed : Visibility.Visible;
        if (_currentLink is not null) LinkHintText.Text = $"Ouvrir {new Uri(_currentLink).Host.Replace("www.", "")}";
    }

    private void LinkHint_Click(object sender, MouseButtonEventArgs e) => OpenCurrentLink();

    private void OpenCurrentLink()
    {
        if (_currentLink is not null) GameLauncher.Open(_currentLink);
    }

    // ---------------- Navigation ----------------

    /// <summary>Redonne le focus à la dernière ligne sélectionnée (ex. après la saisie d'une clé), sinon à la première.</summary>
    public void FocusFirst()
    {
        if (Items.IsKeyboardFocusWithin || Menu.IsKeyboardFocusWithin) return;
        // Retour d'une saisie (clé d'API…) : on reste sur la ligne modifiée ; sinon sur la catégorie.
        UIElement target = _lastFocused is { IsVisible: true } last && last.IsDescendantOf(Items) ? last : Current.MenuButton;
        Dispatcher.BeginInvoke(() => FocusNav.Focus(target), System.Windows.Threading.DispatcherPriority.Input);
    }

    public bool Handle(NavAction action)
    {
        var inMenu = Menu.IsKeyboardFocusWithin;
        switch (action)
        {
            case NavAction.Up or NavAction.Down:
                if (!FocusNav.Move(action, inMenu ? Menu : Items) && !inMenu && !Items.IsKeyboardFocusWithin) FocusNav.Focus(Current.MenuButton);
                return true;
            case NavAction.Right:
                if (inMenu) EnterCategory();
                return true;
            case NavAction.Left:
                if (inMenu) return false; // vers la barre de gauche
                FocusNav.Focus(Current.MenuButton);
                return true;
            case NavAction.Back when !inMenu:
                FocusNav.Focus(Current.MenuButton);
                return true;
            case NavAction.Accept:
                FocusNav.Activate();
                return true;
            case NavAction.Secondary:
                OpenCurrentLink();
                return true;
        }
        return false;
    }
}
