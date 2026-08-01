using GratingPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace GratingPlayer.Controls;

/// <summary>
/// 广告牌单条：正反两面各显示一张大图的对应条带区域。
/// 竖条绕 Y 轴翻转；横条绕 X 轴翻转。图片按 UniformToFill 铺满画板后裁切。
/// </summary>
public sealed class FlipStrip : Grid
{
    private readonly Border _faceHost;
    private readonly Image _frontImage;
    private readonly Image _backImage;
    private readonly PlaneProjection _projection;
    private readonly Rectangle _edgeHighlight;
    private bool _showingFront = true;
    private bool _busy;
    private StripOrientation _orientation = StripOrientation.Vertical;

    public FlipStrip()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        _projection = new PlaneProjection
        {
            CenterOfRotationX = 0.5,
            CenterOfRotationY = 0.5,
            GlobalOffsetZ = 0,
        };
        Projection = _projection;

        _frontImage = CreateFaceImage();
        _backImage = CreateFaceImage();

        _faceHost = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)),
            Child = _frontImage,
        };

        Children.Add(_faceHost);

        _edgeHighlight = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(90, 220, 230, 255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(20, 220, 230, 255), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(70, 220, 230, 255), Offset = 1 },
                },
            },
        };
        Children.Add(_edgeHighlight);
        ApplyChrome(StripOrientation.Vertical);
    }

    private static Image CreateFaceImage() => new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    public bool ShowingFront => _showingFront;
    public bool IsBusy => _busy;

    public void Configure(
        ImageSource frontSource,
        ImageSource backSource,
        int index,
        int stripCount,
        double boardWidth,
        double boardHeight,
        StripOrientation orientation)
    {
        _orientation = orientation;
        ApplyChrome(orientation);

        if (orientation == StripOrientation.Horizontal)
        {
            var stripHeight = Math.Max(1, boardHeight / stripCount);
            Width = boardWidth;
            Height = stripHeight;
            Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, boardWidth, stripHeight),
            };
            ApplyCoverCrop(_frontImage, frontSource, index, stripHeight, boardWidth, boardHeight, orientation);
            ApplyCoverCrop(_backImage, backSource, index, stripHeight, boardWidth, boardHeight, orientation);
        }
        else
        {
            var stripWidth = Math.Max(1, boardWidth / stripCount);
            Width = stripWidth;
            Height = boardHeight;
            Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, stripWidth, boardHeight),
            };
            ApplyCoverCrop(_frontImage, frontSource, index, stripWidth, boardWidth, boardHeight, orientation);
            ApplyCoverCrop(_backImage, backSource, index, stripWidth, boardWidth, boardHeight, orientation);
        }

        _showingFront = true;
        _projection.RotationX = 0;
        _projection.RotationY = 0;
        _faceHost.Child = _frontImage;
        _busy = false;
    }

    private void ApplyChrome(StripOrientation orientation)
    {
        if (orientation == StripOrientation.Horizontal)
        {
            BorderThickness = new Thickness(0, 0.4, 0, 0.4);
            _edgeHighlight.Width = double.NaN;
            _edgeHighlight.Height = 1;
            _edgeHighlight.HorizontalAlignment = HorizontalAlignment.Stretch;
            _edgeHighlight.VerticalAlignment = VerticalAlignment.Top;
            _edgeHighlight.Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(90, 220, 230, 255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(20, 220, 230, 255), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(70, 220, 230, 255), Offset = 1 },
                },
            };
        }
        else
        {
            BorderThickness = new Thickness(0.4, 0, 0.4, 0);
            _edgeHighlight.Width = 1;
            _edgeHighlight.Height = double.NaN;
            _edgeHighlight.HorizontalAlignment = HorizontalAlignment.Left;
            _edgeHighlight.VerticalAlignment = VerticalAlignment.Stretch;
            _edgeHighlight.Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(90, 220, 230, 255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(20, 220, 230, 255), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(70, 220, 230, 255), Offset = 1 },
                },
            };
        }
    }

    /// <summary>
    /// 将整图按 UniformToFill（等比铺满、居中裁切）放入画板后，再偏移裁出当前条带。
    /// </summary>
    private static void ApplyCoverCrop(
        Image image,
        ImageSource source,
        int index,
        double stripSize,
        double boardWidth,
        double boardHeight,
        StripOrientation orientation)
    {
        image.Source = source;
        image.Stretch = Stretch.UniformToFill;
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Top;

        if (!TryGetPixelSize(source, out var imageW, out var imageH) || imageW <= 0 || imageH <= 0)
        {
            image.Width = boardWidth;
            image.Height = boardHeight;
            image.Margin = orientation == StripOrientation.Horizontal
                ? new Thickness(0, -index * stripSize, 0, 0)
                : new Thickness(-index * stripSize, 0, 0, 0);
            return;
        }

        var scale = Math.Max(boardWidth / imageW, boardHeight / imageH);
        var displayW = imageW * scale;
        var displayH = imageH * scale;
        var offsetX = (boardWidth - displayW) / 2.0;
        var offsetY = (boardHeight - displayH) / 2.0;

        image.Width = displayW;
        image.Height = displayH;
        image.Stretch = Stretch.Fill;
        image.Margin = orientation == StripOrientation.Horizontal
            ? new Thickness(offsetX, offsetY - index * stripSize, 0, 0)
            : new Thickness(offsetX - index * stripSize, offsetY, 0, 0);
        image.RenderTransform = null;
    }

    private static bool TryGetPixelSize(ImageSource source, out double width, out double height)
    {
        switch (source)
        {
            case BitmapSource bmp when bmp.PixelWidth > 0 && bmp.PixelHeight > 0:
                width = bmp.PixelWidth;
                height = bmp.PixelHeight;
                return true;
            default:
                width = 0;
                height = 0;
                return false;
        }
    }

    public Task FlipAsync(TimeSpan duration, int direction = 1)
    {
        if (_busy)
            return Task.CompletedTask;

        var dir = direction >= 0 ? 1 : -1;
        _busy = true;
        var tcs = new TaskCompletionSource();
        var half = TimeSpan.FromMilliseconds(Math.Max(40, duration.TotalMilliseconds / 2));
        var axis = _orientation == StripOrientation.Horizontal ? "RotationX" : "RotationY";

        var toEdge = new DoubleAnimation
        {
            From = 0,
            To = 90 * dir,
            Duration = half,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(toEdge, _projection);
        Storyboard.SetTargetProperty(toEdge, axis);

        var sb1 = new Storyboard();
        sb1.Children.Add(toEdge);
        sb1.Completed += (_, _) =>
        {
            _showingFront = !_showingFront;
            _faceHost.Child = _showingFront ? _frontImage : _backImage;

            var fromEdge = new DoubleAnimation
            {
                From = -90 * dir,
                To = 0,
                Duration = half,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fromEdge, _projection);
            Storyboard.SetTargetProperty(fromEdge, axis);

            var sb2 = new Storyboard();
            sb2.Children.Add(fromEdge);
            sb2.Completed += (_, _) =>
            {
                _projection.RotationX = 0;
                _projection.RotationY = 0;
                _busy = false;
                tcs.TrySetResult();
            };
            sb2.Begin();
        };
        sb1.Begin();
        return tcs.Task;
    }
}
