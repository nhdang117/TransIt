using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using TransIt.Models;
using TransIt.Services;

namespace TransIt.Windows.Overlay;

public partial class OverlayCanvas : UserControl
{
    public event EventHandler? CloseRequested;
    private Color _penColor = Colors.Red;
    private Storyboard? _spinnerBoard;

    public OverlayCanvas()
    {
        InitializeComponent();
        InkLayer.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor,
            Width = 3,
            Height = 3,
            FitToCurve = true
        };
    }

    public void RenderItems(IList<OverlayTextItem> items)
    {
        TextLayer.Children.Clear();

        foreach (var item in items)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(item.BackgroundColor),
                Width  = item.ScreenRect.Width,
                Height = item.ScreenRect.Height,
                Padding = new Thickness(2),
                ClipToBounds = true
            };

            var tb = new TextBlock
            {
                Text = item.TranslatedText,
                Foreground = new SolidColorBrush(item.ForegroundColor),
                FontSize = item.FontSize,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };

            border.Child = tb;
            Canvas.SetLeft(border, item.ScreenRect.X);
            Canvas.SetTop(border,  item.ScreenRect.Y);
            TextLayer.Children.Add(border);

            // Shrink font until text fits within the block height.
            // Measure tb directly — border has explicit Height so DesiredSize always equals Height.
            double innerW = item.ScreenRect.Width  - border.Padding.Left - border.Padding.Right;
            double innerH = item.ScreenRect.Height - border.Padding.Top  - border.Padding.Bottom;
            tb.Measure(new Size(innerW, double.PositiveInfinity));
            while (tb.FontSize > 6 && tb.DesiredSize.Height > innerH)
            {
                tb.FontSize -= 1;
                tb.InvalidateMeasure();
                tb.Measure(new Size(innerW, double.PositiveInfinity));
            }
        }
    }

    public void SetFrozenBackground(BitmapSource? img, double windowW = 0, double windowH = 0)
    {
        FrozenLayer.Source = img;
        FrozenLayer.Visibility = img is null ? Visibility.Collapsed : Visibility.Visible;

        // Scale TextLayer from bitmap-pixel space to DIP space so OCR coords align with image
        if (img != null && windowW > 0 && img.PixelWidth > 0)
            TextLayer.RenderTransform = new ScaleTransform(windowW / img.PixelWidth, windowH / img.PixelHeight);
        else
            TextLayer.RenderTransform = Transform.Identity;
    }

    public void ShowAnnotationToolbar(bool show)
    {
        Toolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InkLayer.IsHitTestVisible = show;
    }

    public void ClearInk() => InkLayer.Strokes.Clear();

    public void ShowLoading(bool show)
    {
        LoadingLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) StartSpinner();
        else StopSpinner();
    }

    private void StartSpinner()
    {
        _spinnerBoard?.Stop(this);
        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTargetName(anim, nameof(SpinnerRotate));
        Storyboard.SetTargetProperty(anim, new PropertyPath(RotateTransform.AngleProperty));
        _spinnerBoard = new Storyboard();
        _spinnerBoard.Children.Add(anim);
        _spinnerBoard.Begin(this, true);
    }

    private void StopSpinner()
    {
        _spinnerBoard?.Stop(this);
        _spinnerBoard = null;
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private void BtnPen_Click(object sender, RoutedEventArgs e)
    {
        InkLayer.EditingMode = InkCanvasEditingMode.Ink;
        InkLayer.DefaultDrawingAttributes.Color = _penColor;
    }

    private void BtnColor_Click(object sender, RoutedEventArgs e)
    {
        // Cycle through common colors
        _penColor = _penColor == Colors.Red    ? Colors.Yellow  :
                    _penColor == Colors.Yellow ? Colors.Cyan    :
                    _penColor == Colors.Cyan   ? Colors.Lime    :
                                                 Colors.Red;
        InkLayer.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor,
            Width = InkLayer.DefaultDrawingAttributes.Width,
            Height = InkLayer.DefaultDrawingAttributes.Height,
            FitToCurve = true
        };
    }

    private void BtnErase_Click(object sender, RoutedEventArgs e) =>
        InkLayer.EditingMode = InkCanvasEditingMode.EraseByStroke;

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export overlay",
            Filter = "PNG Image|*.png",
            FileName = $"TransIt_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog() == true)
            ExportService.SaveToPng(this, dlg.FileName);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) =>
        ExportService.CopyToClipboard(this);

    private void BtnClose_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
