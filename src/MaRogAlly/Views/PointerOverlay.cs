using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MaRogAlly.UI;
using Path = System.Windows.Shapes.Path;

namespace MaRogAlly.Views;

/// <summary>
/// Curseur de la souris à la manette : une flèche aux coins arrondis (contour noir, intérieur blanc) dans une toute petite
/// fenêtre, toujours devant, que les clics traversent et qui ne prend jamais le premier plan.
/// </summary>
public sealed class PointerOverlay : Window
{
    /// <summary>Marge autour de la flèche (place de son ombre) ; la pointe est le point visé.</summary>
    private const double Gap = 6, Stroke = 2.6;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>Flèche inclinée vers le haut à gauche, pointe en (0, 0), à la façon du modèle choisi.</summary>
    private static readonly Geometry Arrow = Geometry.Parse("M 0,0 L 22,12.5 L 11.5,16 L 4,26 Z");

    private IntPtr _hwnd;

    public PointerOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 22 + 2 * Gap + Stroke;
        Height = 26 + 2 * Gap + Stroke;
        Left = -1000;
        Top = -1000;

        var arrow = new Path
        {
            Data = Arrow,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)),
            StrokeThickness = Stroke,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.45 },
            Opacity = 0,
        };
        // Grandit et rétrécit depuis la pointe : le point visé ne bouge pas pendant l'animation.
        arrow.RenderTransform = _scale;
        Canvas.SetLeft(arrow, Gap + Stroke / 2);
        Canvas.SetTop(arrow, Gap + Stroke / 2);
        _arrow = arrow;
        Content = new Canvas { Children = { arrow } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
    }

    /// <summary>État caché (petite et transparente) : la première apparition est animée comme les suivantes.</summary>
    private readonly ScaleTransform _scale = new(0.3, 0.3);
    private readonly Path _arrow;
    private static readonly IEasingFunction Pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
    private static readonly IEasingFunction Away = new CubicEase { EasingMode = EasingMode.EaseIn };
    /// <summary>Vrai entre l'apparition et le début de la disparition (le curseur doit être là).</summary>
    private bool _shown;

    /// <summary>Place la pointe de la flèche sur ce point de l'écran (pixels réels) ; la fait apparaître si elle n'y était pas.</summary>
    public void ShowAt(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        var tipX = (int)Math.Round((Gap + Stroke / 2) * dpi.DpiScaleX);
        var tipY = (int)Math.Round((Gap + Stroke / 2) * dpi.DpiScaleY);
        // Reposée tout en haut à chaque fois : une fenêtre ouverte entre-temps ne doit pas passer devant.
        SetWindowPos(_hwnd, HwndTopmost, x - tipX, y - tipY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (_shown) return;
        _shown = true;
        if (!IsVisible) Show();
        // Apparition bien visible : la flèche jaillit de sa pointe en grossissant un peu trop, puis se pose.
        Animate(1, 300, Pop, 1, 160, null);
    }

    /// <summary>Disparition : la flèche rétrécit vers sa pointe en s'effaçant, puis la fenêtre se cache.</summary>
    public void HidePointer()
    {
        if (!_shown) return;
        _shown = false;
        Animate(0.3, 200, Away, 0, 200, () =>
        {
            if (!_shown) Hide();
        });
    }

    private void Animate(double scale, double ms, IEasingFunction ease, double opacity, double fadeMs, Action? done)
    {
        // Repart de l'état du moment : une apparition interrompue par une disparition (ou l'inverse) ne saute pas.
        var grow = new DoubleAnimation(scale, Anim.Ms(ms)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimation(opacity, Anim.Ms(fadeMs)) { EasingFunction = Anim.Out };
        if (done is not null) fade.Completed += (_, _) => done();
        _arrow.BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
