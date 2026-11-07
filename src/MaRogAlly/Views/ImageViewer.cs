using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;

namespace MaRogAlly.Views;

/// <summary>
/// Capture d'écran en grand, comme les images de la fiche d'un jeu : deux calques en fondu enchaîné,
/// et dessous toutes les miniatures, avec un cadre qui glisse jusqu'à l'image affichée.
/// </summary>
public sealed class ImageViewer : OverlayBase
{
    private const int DecodeWidth = 1920, ThumbDecodeWidth = 240;
    private const double StripWidth = 112, StripHeight = 63, StripGap = 10;

    private readonly List<string> _files;
    private readonly Image _a = Layer(), _b = Layer();
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly Border _stripFrame = new() { Width = StripWidth, Height = StripHeight, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), BorderBrush = Brushes.White, IsHitTestVisible = false };
    private readonly TranslateTransform _stripFrameMove = new();
    private readonly ScrollViewer _stripScroll;
    private readonly IEasingFunction _ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private int _index, _loading;
    private bool _onA = true, _shown;

    public ImageViewer(List<string> files, int index)
    {
        _files = files;
        _index = index;
        _stripFrame.RenderTransform = _stripFrameMove;
        _b.Opacity = 0;

        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x05, 0x06, 0x08)) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.MouseLeftButtonUp += (_, _) => Close();
        root.Children.Add(_a);
        root.Children.Add(_b);
        _stripScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            // La place des aides de la manette reste libre en bas de l'écran.
            Margin = new Thickness(64, 0, 64, 64),
            Content = new Grid { Margin = new Thickness(6), Children = { _strip, _stripFrame } },
        };
        Grid.SetRow(_stripScroll, 1);
        root.Children.Add(_stripScroll);
        Content = root;

        BuildStrip();
        Loaded += (_, _) =>
        {
            UpdateStrip(animate: false);
            _ = ShowCurrentAsync();
        };
    }

    private static Image Layer()
    {
        // Centrée à sa taille réelle : l'arrondi suit les bords de l'image.
        var image = new Image { Margin = new Thickness(64, 36, 64, 18), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Rounded.SetRadius(image, 16);
        return image;
    }

    public override string Hints => _files.Count > 1 ? "←→:Image|B:Fermer" : "B:Fermer";

    /// <summary>Aucun bouton ici : le focus reste à la fenêtre, pour que le clavier continue de répondre.</summary>
    protected override void FocusOnOpen() => Window.GetWindow(this)?.Focus();

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Left or NavAction.Right:
                if (_files.Count > 1) Select(_index + (action == NavAction.Left ? -1 : 1));
                return true;
            case NavAction.Accept or NavAction.Back:
                Close();
                return true;
        }
        return false;
    }

    private void Select(int index)
    {
        _index = (index + _files.Count) % _files.Count;
        UpdateStrip(animate: true);
        _ = ShowCurrentAsync();
    }

    private void BuildStrip()
    {
        for (var i = 0; i < _files.Count; i++)
        {
            var index = i;
            var image = new Image { Stretch = Stretch.UniformToFill };
            Rounded.SetRadius(image, 8);
            var frame = new Border
            {
                Width = StripWidth,
                Height = StripHeight,
                Margin = new Thickness(StripGap / 2, 0, StripGap / 2, 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Child = image,
            };
            frame.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Select(index);
            };
            _strip.Children.Add(frame);
            _ = LoadThumbnailAsync(image, _files[i]);
        }
    }

    private static async Task LoadThumbnailAsync(Image image, string path) => image.Source = await Task.Run(() => ScreenCapture.Load(path, ThumbDecodeWidth));

    /// <summary>Le cadre blanc glisse jusqu'à la miniature affichée, qui s'éclaire et grossit légèrement ; les autres s'atténuent.</summary>
    private void UpdateStrip(bool animate)
    {
        var frameX = _index * (StripWidth + StripGap) + StripGap / 2;
        if (animate) _stripFrameMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(frameX, Anim.Ms(260)) { EasingFunction = _ease });
        else
        {
            _stripFrameMove.BeginAnimation(TranslateTransform.XProperty, null);
            _stripFrameMove.X = frameX;
        }
        for (var i = 0; i < _strip.Children.Count; i++)
        {
            var frame = (Border)_strip.Children[i];
            var current = i == _index;
            Anim.Fade(frame, current ? 1 : 0.45, animate ? 220 : 1);
            var scale = (ScaleTransform)frame.RenderTransform;
            var grow = new DoubleAnimation(current ? 1.06 : 1.0, Anim.Ms(animate ? 260 : 1)) { EasingFunction = _ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        // Le carrousel défile pour garder l'image affichée au centre.
        var x = _index * (StripWidth + StripGap) - (_stripScroll.ViewportWidth - StripWidth) / 2;
        Anim.ScrollToH(_stripScroll, Math.Max(0, x));
    }

    private async Task ShowCurrentAsync()
    {
        var loading = ++_loading;
        var picture = await Task.Run(() => ScreenCapture.Load(_files[_index], DecodeWidth));
        // Une image plus récente a été demandée entre-temps.
        if (loading != _loading) return;
        if (!_shown)
        {
            _shown = true;
            _a.Source = picture;
            _a.Opacity = 0;
            _a.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(220)) { EasingFunction = Anim.Out });
            return;
        }
        // Changement d'image : fondu enchaîné entre les deux calques.
        var incoming = _onA ? _b : _a;
        var outgoing = _onA ? _a : _b;
        incoming.Source = picture;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(320)) { EasingFunction = Anim.InOut });
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim.Ms(320)) { EasingFunction = Anim.InOut });
        _onA = !_onA;
    }
}
