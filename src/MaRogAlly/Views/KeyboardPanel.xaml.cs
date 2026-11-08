using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Core;
using MaRogAlly.Input;
using MaRogAlly.Services;
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
        "AZERTYUIOP",
        "QSDFGHJKLM",
        "WXCVBN-':.",
    ];

    /// <summary>Colonnes de la grille des lettres : 12 touches, en demi-touches pour décaler les rangées.</summary>
    private const int KeyColumns = 24;

    /// <summary>Toujours visibles, en bas à droite (lettres comme symboles).</summary>
    private const string Numbers = "1234567890";

    private static readonly string[] Symbols =
    [
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
    private readonly List<Button> _letters = [];
    private Button? _lastKey;
    /// <summary>Dictée en cours (un second appui sur le micro l'annule).</summary>
    private CancellationTokenSource? _listening;

    /// <summary>Appelé à chaque frappe (recherche en direct).</summary>
    public event Action<string>? ValueChanged;
    /// <summary>Appelé à la validation (OK ou Start).</summary>
    public event Action<string>? Submitted;
    /// <summary>Message à afficher (icône, texte).</summary>
    public event Action<string, string>? ToastRequested;

    public KeyboardPanel(string initial, string placeholder = "", bool lowercase = false)
    {
        InitializeComponent();
        _lowercase = lowercase;
        _autoCapital = !lowercase;
        _highlight = new SlideHighlight(KeyHighlight, KeysHost);
        Placeholder.Text = placeholder;

        for (var i = 0; i < KeyColumns; i++) Keys.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < Layout.Length; row++)
            for (var col = 0; col < Layout[row].Length; col++)
            {
                var b = new Button { Style = (Style)FindResource("Key") };
                b.Click += (_, _) => Type((string)b.Content);
                Grid.SetRow(b, row);
                Grid.SetColumn(b, row + col * 2);
                Grid.SetColumnSpan(b, 2);
                // Avant les touches de fonction : la sélection s'ouvre sur la première lettre.
                Keys.Children.Insert(_letters.Count, b);
                _letters.Add(b);
            }
        foreach (var digit in Numbers)
        {
            var b = new Button { Style = (Style)FindResource("Key"), Content = digit.ToString(), FontSize = 20 };
            b.Click += (_, _) => Type((string)b.Content);
            Digits.Children.Add(b);
        }
        // À la manette, la sélection glisse d'une touche à l'autre (ce cadre est commun à tout le clavier) ; au doigt ou à la souris, la touche s'enfonce.
        foreach (var b in KeyRows().SelectMany(r => r))
        {
            b.GotKeyboardFocus += (_, _) =>
            {
                _lastKey = b;
                KeyHighlight.CornerRadius = (CornerRadius)b.Tag;
                if (PadSelection) _highlight.MoveTo(b);
            };
            AddPressAnimation(b);
        }
        ApplyKeys();

        // Pour les clés d'API (longues), « Coller » est bien plus pratique que la dictée.
        if (lowercase)
        {
            VoiceButton.Content = "Coller";
            VoiceButton.Click += Paste_Click;
        }
        else VoiceButton.Click += Voice_Click;
        // Une phrase reconnue après la fermeture ne doit plus rien saisir.
        CloseRequested += _ => _listening?.Cancel();
        Set(initial);
    }

    public override void OnOpened()
    {
        // Pas de fondu de tout le panneau (une couche transparente de plus à composer à chaque image) : seul le voile
        // apparaît en fondu, et le clavier monte du bas de l'écran, photographié une fois pour toutes le temps du trajet.
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(220)) { EasingFunction = Anim.Out });
        Sheet.CacheMode = new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX };
        var rise = new DoubleAnimation(560, 0, Anim.Ms(340)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
        rise.Completed += (_, _) => Sheet.CacheMode = null;
        var move = new TranslateTransform();
        Sheet.RenderTransform = move;
        move.BeginAnimation(TranslateTransform.YProperty, rise);
        Dispatcher.BeginInvoke(FocusOnOpen, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Construit un clavier une première fois en coulisse : la première ouverture n'a plus à tout préparer.</summary>
    public static void Warm(Size size)
    {
        var keyboard = new KeyboardPanel("", "");
        keyboard.Measure(size);
        keyboard.Arrange(new Rect(size));
        keyboard.UpdateLayout();
    }
    public override string Hints => "A:Taper|X:Effacer|Y:Espace|LB:Maj/min|RB:Symboles|≡:Entrer|B:Fermer";

    /// <summary>La sélection s'ouvre sur la première lettre, pas sur la remise à zéro.</summary>
    protected override FrameworkElement FocusScope => Keys;

    private const double PressedScale = 0.9;
    private const string MicIcon = "\uE720";

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
        key.PreviewMouseLeftButtonUp += (_, _) => Animate(new DoubleAnimation(PressedScale, 1, Anim.Ms(200)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } });
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
        foreach (var (key, label) in _letters.Zip(labels))
            key.Content = _lowercase ? char.ToLowerInvariant(label).ToString() : label.ToString();
        ShiftText.Text = ShiftRightText.Text = _lowercase ? "Maj" : "MAJ";
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

    /// <summary>Rangées de touches, de haut en bas : remise à zéro, lettres (avec Effacer, Entrer et Maj), puis touches de fonction et chiffres.</summary>
    private List<List<Button>> KeyRows()
    {
        var letters = _letters.Chunk(Numbers.Length).ToList();
        return
        [
            [ResetButton],
            [.. letters[0], BackspaceButton],
            [.. letters[1], EnterButton],
            [ShiftButton, .. letters[2], ShiftRightButton],
            [.. Specials.Children.OfType<Button>(), .. Digits.Children.OfType<Button>()],
        ];
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

    /// <summary>Lance la dictée sans toucher le clavier (vérification de l'affichage).</summary>
    public void PreviewVoice() => Voice_Click(this, new RoutedEventArgs());

    private async void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (_listening is { } active)
        {
            active.Cancel();
            return;
        }
        using var cancel = _listening = new CancellationTokenSource();
        ShowListening(true);
        try
        {
            var heard = await VoiceInput.ListenAsync(cancel.Token);
            if (heard.Length == 0) ToastRequested?.Invoke(MicIcon, "Je n'ai rien entendu : vérifie le micro");
            else Type(_value.Length == 0 || _value.EndsWith(' ') ? heard : $" {heard}");
        }
        catch (OperationCanceledException)
        {
            // Annulée par un second appui ou par la fermeture du clavier.
        }
        catch (VoiceInputException ex)
        {
            ToastRequested?.Invoke(MicIcon, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Dictée", ex);
            ToastRequested?.Invoke(MicIcon, "La dictée a échoué (voir log.txt)");
        }
        finally
        {
            _listening = null;
            ShowListening(false);
        }
    }

    /// <summary>Micro allumé qui pulse et « Je t'écoute… » dans le champ, le temps de la dictée.</summary>
    private void ShowListening(bool listening)
    {
        ListeningText.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        if (listening)
        {
            VoiceButton.SetResourceReference(BackgroundProperty, "Accent");
            VoiceButton.Foreground = Brushes.White;
        }
        else
        {
            VoiceButton.ClearValue(BackgroundProperty);
            VoiceButton.ClearValue(ForegroundProperty);
        }
        VoiceIcon.BeginAnimation(OpacityProperty, listening
            ? new DoubleAnimation(1, 0.35, Anim.Ms(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText()) Set(Clipboard.GetText().Trim());
    }

    private void Submit()
    {
        Submitted?.Invoke(_value);
        Close();
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();
    private void Backspace_Click(object sender, RoutedEventArgs e) => Backspace();
    private void Space_Click(object sender, RoutedEventArgs e) => Type(" ");
    private void Clear_Click(object sender, RoutedEventArgs e) => Set("");
    private void Ok_Click(object sender, RoutedEventArgs e) => Submit();
    private void Shift_Click(object sender, RoutedEventArgs e) => ToggleCase();
    private void Symbols_Click(object sender, RoutedEventArgs e) => ToggleSymbols();
}
