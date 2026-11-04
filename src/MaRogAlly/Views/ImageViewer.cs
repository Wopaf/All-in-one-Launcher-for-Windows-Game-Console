using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MaRogAlly.Input;
using MaRogAlly.Services;
using MaRogAlly.UI;
using Path = System.IO.Path;

namespace MaRogAlly.Views;

/// <summary>Capture d'écran en grand, au centre de l'écran et aux coins arrondis. Gauche et droite passent à l'image voisine.</summary>
public sealed class ImageViewer : OverlayBase
{
    private const int DecodeWidth = 1920;

    private readonly List<string> _files;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Border _frame;
    private readonly ScaleTransform _zoom = new();
    private readonly TextBlock _name = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 56) };
    private int _index, _loading;
    private bool _shown;

    public ImageViewer(List<string> files, int index)
    {
        _files = files;
        _index = index;
        _name.Foreground = (Brush)FindResource("TextSecondary");
        _frame = new Border
        {
            Margin = new Thickness(64, 40, 64, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(20),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _zoom,
            Child = _image,
        };
        Rounded.SetRadius(_frame, 20);

        var backdrop = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)) };
        backdrop.MouseLeftButtonUp += (_, _) => Close();
        var layout = new Grid { RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        layout.Children.Add(_frame);
        Grid.SetRow(_name, 1);
        layout.Children.Add(_name);
        Content = new Grid { Children = { backdrop, layout } };
        _ = ShowCurrentAsync();
    }

    public override string Hints => _files.Count > 1 ? "←→:Précédente / suivante|B:Retour" : "B:Retour";

    /// <summary>Aucun bouton ici : le focus reste à la fenêtre, pour que le clavier continue de répondre.</summary>
    protected override void FocusOnOpen() => Window.GetWindow(this)?.Focus();

    public override bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Left or NavAction.Right:
                if (_files.Count < 2) return true;
                _index = (_index + (action == NavAction.Left ? -1 : 1) + _files.Count) % _files.Count;
                _ = ShowCurrentAsync();
                return true;
            case NavAction.Accept or NavAction.Back:
                Close();
                return true;
        }
        return false;
    }

    private async Task ShowCurrentAsync()
    {
        var loading = ++_loading;
        var path = _files[_index];
        _name.Text = $"{_index + 1} / {_files.Count}  ·  {Path.GetFileNameWithoutExtension(path)}";
        var picture = await Task.Run(() => ScreenCapture.Load(path, DecodeWidth));
        // Une image plus récente a été demandée entre-temps.
        if (loading != _loading) return;
        _image.Source = picture;
        _frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(_shown ? 160 : 240)) { EasingFunction = Anim.Out });
        if (!_shown)
        {
            var grow = new DoubleAnimation(0.93, 1, Anim.Ms(280)) { EasingFunction = Anim.Out };
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        _shown = true;
    }
}
