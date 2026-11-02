using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Input;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Clavier virtuel AZERTY pilotable à la manette (recherche, saisie des clés d'API), posé en bas de l'écran
/// pour être aussi facile à atteindre avec les doigts, aux couleurs du thème.
/// </summary>
public partial class KeyboardPanel : OverlayBase
{
    private static readonly string[] Layout =
    [
        "1234567890",
        "AZERTYUIOP",
        "QSDFGHJKLM",
        "WXCVBN-':.",
    ];

    private static readonly string[] Symbols =
    [
        "1234567890",
        "@#&_-+=/\\*",
        "!?()[]<>~|",
        "\"',;:.%€$°",
    ];

    /// <summary>Faux dès qu'on touche le clavier au doigt ou à la souris : le cadre coloré ne sert qu'à la manette.</summary>
    public static readonly DependencyProperty PadSelectionProperty = DependencyProperty.Register(nameof(PadSelection), typeof(bool), typeof(KeyboardPanel), new PropertyMetadata(true));
    public bool PadSelection { get => (bool)GetValue(PadSelectionProperty); set => SetValue(PadSelectionProperty, value); }

    private string _value = "";
    private bool _lowercase;
    /// <summary>Majuscule automatique pour la première lettre uniquement (pas pour les clés d'API).</summary>
    private readonly bool _autoCapital;
    private bool _symbols;
    private readonly SlideHighlight _highlight;
    private Button? _lastKey;

    /// <summary>Appelé à chaque frappe (recherche en direct).</summary>
    public event Action<string>? ValueChanged;
    /// <summary>Appelé à la validation (OK ou Start).</summary>
    public event Action<string>? Submitted;

    public KeyboardPanel(string initial, string placeholder = "", bool lowercase = false)
    {
        InitializeComponent();
        _lowercase = lowercase;
        _autoCapital = !lowercase;
        _highlight = new SlideHighlight(KeyHighlight, KeysHost);
        Placeholder.Text = placeholder;

        foreach (var _ in Layout.SelectMany(row => row))
        {
            var b = new Button { Style = (Style)FindResource("Key") };
            b.Click += (_, _) => Type((string)b.Content);
            Keys.Children.Add(b);
        }
        // À la manette, la sélection glisse d'une touche à l'autre (ce cadre est commun à tout le clavier) ; au doigt ou à la souris, la touche s'enfonce.
        foreach (var b in KeyRows().SelectMany(r => r))
        {
            b.GotKeyboardFocus += (_, _) =>
            {
                _lastKey = b;
                if (PadSelection) _highlight.MoveTo(b);
            };
            AddPressAnimation(b);
        }
        ApplyKeys();

        // Pour les clés d'API (longues), « Coller » est bien plus pratique qu'« Effacer ».
        if (lowercase)
        {
            ExtraButton.Content = "Coller";
            ExtraButton.Click -= Clear_Click;
            ExtraButton.Click += (_, _) => { if (Clipboard.ContainsText()) Set(Clipboard.GetText().Trim()); };
        }
        Set(initial);
    }

    public override void OnOpened()
    {
        base.OnOpened();
        // Le clavier monte depuis le bas de l'écran.
        Anim.Enter(Panel, 0, 90, 380);
    }
    public override string Hints => "A:Taper|X:Effacer|Y:Espace|LB:Maj/min|RB:Symboles|≡:Valider|B:Fermer";

    private const double PressedScale = 0.9;

    /// <summary>La touche s'enfonce à l'appui et revient au relâchement (le retour part toujours du fond : un appui bref se voit aussi).</summary>
    private void AddPressAnimation(Button key)
    {
        var scale = new ScaleTransform();
        key.RenderTransform = scale;
        key.RenderTransformOrigin = new Point(0.5, 0.5);
        void Animate(DoubleAnimation animation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        key.PreviewMouseLeftButtonDown += (_, _) =>
        {
            ShowPointerMode();
            Animate(new DoubleAnimation(PressedScale, Anim.Ms(50)) { EasingFunction = Anim.Out });
        };
        key.PreviewMouseLeftButtonUp += (_, _) => Animate(new DoubleAnimation(PressedScale, 1, Anim.Ms(170)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } });
    }

    private void ShowPointerMode()
    {
        if (!PadSelection) return;
        PadSelection = false;
        _highlight.Hide();
    }

    /// <summary>Retour à la manette : le cadre coloré réapparaît sur la touche sélectionnée.</summary>
    private void ShowPadMode()
    {
        if (PadSelection) return;
        PadSelection = true;
        if (Keyboard.FocusedElement is Button key && KeyRows().Any(r => r.Contains(key))) _highlight.MoveTo(key);
    }

    private void ApplyKeys()
    {
        var labels = string.Concat(_symbols ? Symbols : Layout);
        foreach (var (key, label) in Keys.Children.OfType<Button>().Zip(labels))
            key.Content = _lowercase ? char.ToLowerInvariant(label).ToString() : label.ToString();
        ShiftText.Text = _lowercase ? "Maj" : "MAJ";
        SymbolsButton.Content = _symbols ? "ABC" : "@";
    }

    private void ToggleCase()
    {
        _lowercase = !_lowercase;
        ApplyKeys();
    }

    private void ToggleSymbols()
    {
        _symbols = !_symbols;
        ApplyKeys();
    }

    public string Value => _value;

    public void Type(string text) => Set(_value + text);

    private void Set(string value)
    {
        _value = value;
        ValueText.Text = value;
        // Champ vide : majuscule pour la première lettre ; toute saisie repasse en minuscules (Maj ne vaut que pour une frappe).
        if (_autoCapital && _lowercase != value.Length > 0)
        {
            _lowercase = value.Length > 0;
            ApplyKeys();
        }
        Placeholder.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ValueChanged?.Invoke(value);
    }

    public void Backspace()
    {
        if (_value.Length > 0) Set(_value[..^1]);
    }

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                ShowPadMode();
                MoveFocus(action);
                return true;
            case NavAction.Accept when Keyboard.FocusedElement is not Button:
                ShowPadMode();
                MoveFocus(action);
                return true;
            case NavAction.Option:
                Backspace();
                return true;
            case NavAction.Secondary:
                Type(" ");
                return true;
            case NavAction.Menu:
                Submit();
                return true;
            case NavAction.Accept:
                ShowPadMode();
                break;
            case NavAction.PrevTab:
                ToggleCase();
                return true;
            case NavAction.NextTab:
                ToggleSymbols();
                return true;
        }
        return base.Handle(action);
    }

    /// <summary>Rangées de touches, de haut en bas : chiffres, lettres, puis touches de fonction.</summary>
    private List<List<Button>> KeyRows()
    {
        var rows = Keys.Children.OfType<Button>().Chunk(Layout[0].Length).Select(r => r.ToList()).ToList();
        rows.Add(Specials.Children.OfType<Button>().ToList());
        return rows;
    }

    /// <summary>
    /// Déplacement explicite entre les touches : la prédiction de WPF ne trouvait pas toujours la touche voisine
    /// et bloquait la sélection. Gauche / droite bouclent sur la rangée, haut / bas visent la touche la plus proche.
    /// </summary>
    private void MoveFocus(NavAction action)
    {
        var rows = KeyRows();
        var current = Keyboard.FocusedElement as Button;
        var row = current is null ? -1 : rows.FindIndex(r => r.Contains(current));
        if (row < 0)
        {
            // Sélection perdue : on la remet sur la dernière touche utilisée.
            FocusNav.Focus(_lastKey ?? rows[1][0]);
            return;
        }
        var keys = rows[row];
        var col = keys.IndexOf(current!);
        switch (action)
        {
            case NavAction.Left:
                FocusNav.Focus(keys[(col + keys.Count - 1) % keys.Count]);
                break;
            case NavAction.Right:
                FocusNav.Focus(keys[(col + 1) % keys.Count]);
                break;
            case NavAction.Up when row > 0:
                FocusNav.Focus(Nearest(rows[row - 1], current!));
                break;
            case NavAction.Down when row < rows.Count - 1:
                FocusNav.Focus(Nearest(rows[row + 1], current!));
                break;
        }
    }

    private Button Nearest(List<Button> row, Button from)
    {
        double CenterX(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), KeysHost).X;
        var x = CenterX(from);
        return row.MinBy(b => Math.Abs(CenterX(b) - x))!;
    }

    private void Submit()
    {
        Submitted?.Invoke(_value);
        Close();
    }

    private void Backspace_Click(object sender, RoutedEventArgs e) => Backspace();
    private void Space_Click(object sender, RoutedEventArgs e) => Type(" ");
    private void Clear_Click(object sender, RoutedEventArgs e) => Set("");
    private void Ok_Click(object sender, RoutedEventArgs e) => Submit();
    private void Shift_Click(object sender, RoutedEventArgs e) => ToggleCase();
    private void Symbols_Click(object sender, RoutedEventArgs e) => ToggleSymbols();
}
