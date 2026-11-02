using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaRogAlly.Input;
using MaRogAlly.Models;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Avant d'ouvrir la page d'un jeu dans une plateforme (Epic, Steam…) : l'application s'assombrit,
/// un bouton confirme, puis « Ouverture de … en cours » s'affiche quelques secondes le temps que le lanceur s'ouvre.
/// </summary>
public partial class StorePrompt : OverlayBase
{
    private readonly PlatformInfo _platform;
    private readonly Func<bool> _open;
    private Action? _alt;
    private bool _opening;

    public StorePrompt(string gameTitle, PlatformInfo platform, Func<bool> open)
    {
        InitializeComponent();
        _platform = platform;
        _open = open;
        GameTitle.Text = gameTitle;
        PlatformLogo.Data = Geometry.Parse(platform.Logo);
        LogoGlow.Color = (Color)ColorConverter.ConvertFromString(platform.Color);
        OpenText.Text = $"Voir la page du jeu sur {platform.Name}";
    }

    /// <summary>Deuxième bouton (ex. « Rechercher sur Instant Gaming ») : ferme cet écran puis lance l'action.</summary>
    public void AddAlternative(string label, Action run)
    {
        _alt = run;
        AltText.Text = label;
        AltButton.Visibility = Visibility.Visible;
    }

    protected override FrameworkElement AnimatedPart => Body;
    protected override double EnterFromX => 0;

    /// <summary>« de Steam », « d'Epic Games ».</summary>
    private static string Of(string name) => "AEIOUYÉÈaeiouy".Contains(name[0]) ? $"d'{name}" : $"de {name}";

    private void Open_Click(object sender, RoutedEventArgs e) => StartOpening(_platform.Name, _open);

    private void Alt_Click(object sender, RoutedEventArgs e)
    {
        if (_alt is null || _opening) return;
        Close();
        _alt();
    }

    private void StartOpening(string name, Func<bool> open)
    {
        if (_opening) return;
        _opening = true;
        Buttons.Visibility = Visibility.Collapsed;
        Opening.Visibility = Visibility.Visible;
        Anim.Enter(Opening, 0, 8, 260);
        RaiseHintsChanged();

        var ok = open();
        OpeningText.Text = ok ? $"Ouverture {Of(name)} en cours…" : $"Impossible d'ouvrir {name}";
        Spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        // L'écran disparaît de lui-même, le lanceur ayant pris le relais.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    public override bool Handle(NavAction action)
    {
        // Pendant l'ouverture, on laisse l'écran se refermer tout seul.
        if (_opening) return true;
        return base.Handle(action);
    }

    public override string Hints => _opening ? "" : AltButton.Visibility == Visibility.Visible ? "A:Ouvrir|←→:Choisir|B:Annuler" : "A:Ouvrir|B:Annuler";
}
