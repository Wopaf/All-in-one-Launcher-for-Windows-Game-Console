using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private string _value = "";
    private bool _lowercase;
    /// <summary>Majuscule automatique pour la première lettre uniquement (pas pour les clés d'API).</summary>
    private readonly bool _autoCapital;
    private readonly List<Button> _letters = [];
    private readonly SlideHighlight _highlight;
    private Button? _lastKey;

    /// <summary>Appelé à chaque frappe (recherche en direct).</summary>
    public event Action<string>? ValueChanged;
    /// <summary>Appelé à la validation (OK ou Start).</summary>
    public event Action<string>? Submitted;

    public KeyboardPanel(string title, string initial, string placeholder = "", bool lowercase = false)
    {
        InitializeComponent();
        _lowercase = lowercase;
        _autoCapital = !lowercase;
        _highlight = new SlideHighlight(KeyHighlight, KeysHost);
        TitleText.Text = title.ToUpperInvariant();
        Placeholder.Text = placeholder;

        foreach (var row in Layout)
        foreach (var ch in row)
        {
            var b = new Button { Content = ch.ToString(), Style = (Style)FindResource("Key") };
            b.Click += (_, _) => Type((string)b.Content);
            if (char.IsLetter(ch)) _letters.Add(b);
            Keys.Children.Add(b);
        }
        // La sélection glisse d'une touche à l'autre (ce cadre est commun à tout le clavier).
        foreach (var b in KeyRows().SelectMany(r => r))
            b.GotKeyboardFocus += (_, _) =>
            {
                _lastKey = b;
                _highlight.MoveTo(b);
            };
        ApplyCase();

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
    public override string Hints => "A:Taper|X:Effacer|Y:Espace|LB:Maj/min|≡:Valider|B:Fermer";

    private void ApplyCase()
    {
        foreach (var b in _letters)
            b.Content = _lowercase ? ((string)b.Content).ToLowerInvariant() : ((string)b.Content).ToUpperInvariant();
        ShiftText.Text = _lowercase ? "Maj" : "MAJ";
    }

    private void ToggleCase()
    {
        _lowercase = !_lowercase;
        ApplyCase();
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
            ApplyCase();
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
                MoveFocus(action);
                return true;
            case NavAction.Accept when Keyboard.FocusedElement is not Button:
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
            case NavAction.PrevTab or NavAction.NextTab:
                ToggleCase();
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
}
