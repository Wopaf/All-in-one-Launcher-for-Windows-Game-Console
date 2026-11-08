using System.Windows;
using System.Windows.Controls;
using MaRogAlly.Input;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>Panneau affiché par-dessus l'interface (fiche, filtres, tri, clavier). B le referme.</summary>
public class OverlayBase : UserControl
{
    public event Action<OverlayBase>? CloseRequested;
    /// <summary>Les aides de bas d'écran ont changé (mode interne du panneau).</summary>
    public event Action? HintsChanged;
    protected void RaiseHintsChanged() => HintsChanged?.Invoke();

    /// <summary>Élément animé à l'ouverture (glisse depuis la droite par défaut).</summary>
    protected virtual FrameworkElement? AnimatedPart => null;
    protected virtual double EnterFromX => 60;
    /// <summary>Durées d'ouverture : fondu du panneau, glissement de sa partie animée.</summary>
    protected virtual double FadeInMs => 220;
    protected virtual double EnterMs => 360;

    public OverlayBase()
    {
        Focusable = false;
    }

    public virtual void OnOpened()
    {
        Anim.Enter(this, 0, 0, FadeInMs);
        if (AnimatedPart is { } part) Anim.Enter(part, EnterFromX, 0, EnterMs);
        Dispatcher.BeginInvoke(FocusOnOpen, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Sortie : glissement horizontal et durée du fondu de fermeture.</summary>
    public virtual double ExitToX => 30;
    public virtual double ExitMs => 180;

    /// <summary>Le panneau commence à se refermer (son fondu de sortie démarre en même temps).</summary>
    public virtual void OnClosing() { }

    /// <summary>Sélection à l'ouverture.</summary>
    protected virtual void FocusOnOpen() => FocusNav.FocusFirst(FocusScope);

    /// <summary>Le panneau posé au-dessus vient de se refermer.</summary>
    public virtual void OnRevealed() { }

    /// <summary>Zone où le focus circule (par défaut tout le panneau).</summary>
    protected virtual FrameworkElement FocusScope => this;

    public virtual bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Back:
                Close();
                return true;
            case NavAction.Accept:
                // Sélection hors du panneau : A l'y ramène, sans jamais valider ce qui est dessous.
                if (FocusNav.IsFocusInside(this)) FocusNav.Activate();
                else FocusNav.FocusFirst(FocusScope);
                return true;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                FocusNav.Move(action, FocusScope);
                return true;
        }
        return false;
    }

    public void Close() => CloseRequested?.Invoke(this);

    /// <summary>Aides en bas d'écran, au format « Bouton:Libellé|… ».</summary>
    public virtual string Hints => "A:Valider|B:Retour";
}
