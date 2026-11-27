using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using MaRogAlly.Core;
using MaRogAlly.Views;

namespace MaRogAlly.Services;

/// <summary>
/// Clavier tactile de Windows, ouvert quand la souris à la manette clique dans un champ de texte (barre de recherche,
/// adresse d'un site…) : Windows ne l'ouvre de lui-même qu'au toucher de l'écran, pas pour un clic.
/// </summary>
public static class TouchKeyboard
{
    /// <summary>Temps laissé à l'application pour donner le focus au champ cliqué.</summary>
    private const int FocusDelayMs = 180;
    private static int _busy;

    /// <summary>Après un clic à cet endroit (pixels de l'écran) : ouvre le clavier si un champ de texte y a pris le focus.</summary>
    public static void AfterClick(int x, int y)
    {
        // Un seul examen à la fois : une rafale de clics n'en lance pas dix.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FocusDelayMs);
                if (IsTextFieldFocusedAt(x, y) && !IsShown()) await ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Clavier tactile", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    /// <summary>Le champ qui a le focus est un champ de texte modifiable, et le clic est tombé dedans.</summary>
    private static bool IsTextFieldFocusedAt(int x, int y)
    {
        AutomationElement? focused;
        try { focused = AutomationElement.FocusedElement; }
        catch { return false; }
        if (focused is null) return false;
        try
        {
            var current = focused.Current;
            var text = current.ControlType == ControlType.Edit
                       || current.ControlType == ControlType.Document && PointerShapeProbe.IsEditable(focused);
            if (!text || !current.IsEnabled) return false;
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly) return false;
            // Le focus a pu rester sur un champ cliqué plus tôt : seul compte celui qu'on vient de cliquer.
            var bounds = current.BoundingRectangle;
            return bounds.IsEmpty || bounds.Contains(new System.Windows.Point(x, y));
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>Le clavier tactile est déjà à l'écran (il occupe une zone non vide).</summary>
    public static bool IsShown()
    {
        try
        {
            var pane = (IFrameworkInputPane)new FrameworkInputPane();
            return pane.Location(out var r) == 0 && r.Right > r.Left && r.Bottom > r.Top;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ouvre le clavier ; son service est lancé d'abord s'il ne tourne pas encore.</summary>
    private static async Task ShowAsync()
    {
        if (TryToggle()) return;
        var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
        if (!File.Exists(tabTip)) return;
        Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true })?.Dispose();
        // Le service met un instant à répondre ; selon la version de Windows, le lancer suffit déjà à ouvrir le clavier.
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(150);
            if (IsShown() || TryToggle()) return;
        }
    }

    private static bool TryToggle()
    {
        try
        {
            ((ITipInvocation)new UiHostNoLaunch()).Toggle(GetDesktopWindow());
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UiHostNoLaunch;

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [ComImport, Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
    private class FrameworkInputPane;

    [ComImport, Guid("5752238B-24F0-495A-82F1-2FD593056796"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameworkInputPane
    {
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.IUnknown)] object window, [MarshalAs(UnmanagedType.IUnknown)] object handler, out int cookie);
        [PreserveSig] int AdviseWithHWND(IntPtr hwnd, [MarshalAs(UnmanagedType.IUnknown)] object handler, out int cookie);
        [PreserveSig] int Unadvise(int cookie);
        [PreserveSig] int Location(out Rect location);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
}
