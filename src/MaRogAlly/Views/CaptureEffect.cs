using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MaRogAlly.Views;

/// <summary>
/// Animation d'une capture d'écran, jouée par-dessus le jeu juste après la prise : un flash blanc, puis l'image capturée
/// se détache de l'écran, rétrécit en arrondissant ses coins et file vers la gauche, où elle plonge dans le volet qui revient.
/// Fenêtre transparente, devant tout, que les clics traversent et qui ne prend jamais le premier plan.
/// </summary>
public sealed class CaptureEffect : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    /// <summary>Le volet commence à revenir à ce moment, pendant que la vignette plonge vers lui.</summary>
    private const double PanelBackMs = 900;
    /// <summary>Fin de l'animation : la fenêtre se ferme.</summary>
    private const double EndMs = 1350;
    /// <summary>Taille de la vignette, par rapport à l'écran, quand elle se pose avant de plonger.</summary>
    private const double ThumbScale = 0.3;

    private readonly Border _frame;
    private readonly Rectangle _flash = new() { Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
    private readonly RectangleGeometry _clip;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _move = new();
    private readonly DropShadowEffect _shadow = new() { Color = Colors.Black, BlurRadius = 60, ShadowDepth = 0, Opacity = 0 };
    private readonly double _targetX;

    /// <param name="frame">Image capturée (pixels réels de l'écran principal).</param>
    /// <param name="targetX">Centre horizontal de la zone où la vignette plonge (le volet), en points.</param>
    public CaptureEffect(BitmapSource frame, double targetX)
    {
        _targetX = targetX;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 0, 0);
        var image = new Image { Source = frame, Stretch = Stretch.Fill, Clip = _clip };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _frame = new Border
        {
            Child = image,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _scale, _move } },
            Effect = _shadow,
        };
        Content = new Grid { Children = { _frame, _flash } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
    }

    /// <summary>Joue l'animation ; la tâche se termine quand le volet doit revenir (la fin se joue toute seule, puis la fenêtre se ferme).</summary>
    public async Task PlayAsync()
    {
        Show();
        // 1. Flash : monte d'un coup, retombe plus doucement.
        _flash.BeginAnimation(OpacityProperty, Frames((0, 0, null), (70, 0.8, Ease(EasingMode.EaseOut)), (420, 0, Ease(EasingMode.EaseOut))));

        // 2. L'image se détache : elle rétrécit, s'arrondit et file vers la gauche, avec une ombre qui la décolle du jeu.
        var shrink = Frames((0, 1, null), (160, 1, null), (820, ThumbScale, Smooth()), (EndMs, ThumbScale * 0.72, Ease(EasingMode.EaseIn)));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var toX = _targetX - Width / 2;
        _move.BeginAnimation(TranslateTransform.XProperty, Frames((0, 0, null), (160, 0, null), (820, toX, Smooth()), (EndMs, toX - 70, Ease(EasingMode.EaseIn))));
        _move.BeginAnimation(TranslateTransform.YProperty, Frames((0, 0, null), (160, 0, null), (820, -Height * 0.06, Smooth())));
        // Coins arrondis à l'écran (environ 18 points une fois réduite) : le rayon est donné à la taille réelle de l'image.
        var radius = Frames((0, 0, null), (160, 0, null), (820, 18 / ThumbScale, Smooth()));
        _clip.BeginAnimation(RectangleGeometry.RadiusXProperty, radius);
        _clip.BeginAnimation(RectangleGeometry.RadiusYProperty, radius);
        _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, Frames((0, 0, null), (160, 0, null), (600, 0.6, Smooth())));

        // 3. Elle plonge dans le volet en s'effaçant pendant qu'il revient.
        _frame.BeginAnimation(OpacityProperty, Frames((0, 1, null), (PanelBackMs + 80, 1, null), (EndMs, 0, Ease(EasingMode.EaseIn))));

        _ = Task.Delay(TimeSpan.FromMilliseconds(EndMs + 60)).ContinueWith(_ => Dispatcher.BeginInvoke(Close));
        await Task.Delay(TimeSpan.FromMilliseconds(PanelBackMs));
    }

    private static IEasingFunction Smooth() => new QuinticEase { EasingMode = EasingMode.EaseInOut };
    private static IEasingFunction Ease(EasingMode mode) => new CubicEase { EasingMode = mode };

    /// <summary>Images clés (temps en millisecondes, valeur, courbe pour y arriver).</summary>
    private static DoubleAnimationUsingKeyFrames Frames(params (double Ms, double Value, IEasingFunction? Ease)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, value, ease) in frames)
        {
            var at = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
            animation.KeyFrames.Add(ease is null ? new DiscreteDoubleKeyFrame(value, at) : new EasingDoubleKeyFrame(value, at, ease));
        }
        return animation;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
