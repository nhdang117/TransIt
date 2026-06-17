using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using TransIt.Models;
using TransIt.Services;

namespace TransIt.Windows.Overlay;

public partial class OverlayCanvas : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? AddRegionRequested;
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
        DebugLayer.Children.Clear();

        foreach (var item in items)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(item.BackgroundColor),
                BorderThickness = new Thickness(1),
                Width = item.ScreenRect.Width,
                Height = item.ScreenRect.Height,
                ClipToBounds = true,
            };

            var tb = new TextBlock
            {
                Text = item.TranslatedText,
                Foreground = new SolidColorBrush(item.ForegroundColor),
                FontSize = item.FontSize,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            };

            border.Child = tb;
            Canvas.SetLeft(border, item.ScreenRect.X);
            Canvas.SetTop(border,  item.ScreenRect.Y);
            TextLayer.Children.Add(border);
        }
    }

    // Ctrl+1 test mode: gray fill = merge-zone expand rects (GroupLinesInRegion threshold),
    // yellow = raw PaddleOCR line boxes, red = paragraph blocks, cyan dashed = PP-Structure regions.
    public void RenderDebugRects(IList<Rect> lineRects, IList<Rect> blockRects,
                                  IList<Rect>? regionRects = null, IList<Rect>? mergeZoneRects = null)
    {
        DebugLayer.Children.Clear();

        var mergeZoneBrush = new SolidColorBrush(Color.FromArgb(55, 160, 160, 160));
        foreach (var r in mergeZoneRects ?? [])
        {
            var shape = new System.Windows.Shapes.Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Fill = mergeZoneBrush,
                StrokeThickness = 0,
            };
            Canvas.SetLeft(shape, r.X);
            Canvas.SetTop(shape, r.Y);
            DebugLayer.Children.Add(shape);
        }

        foreach (var r in regionRects ?? [])
            DebugLayer.Children.Add(MakeRect(r, Colors.Cyan, 3, dashed: true));

        foreach (var r in lineRects)
            DebugLayer.Children.Add(MakeRect(r, Colors.Yellow, 1));

        foreach (var r in blockRects)
            DebugLayer.Children.Add(MakeRect(r, Colors.Red, 2));
    }

    private static System.Windows.Shapes.Rectangle MakeRect(
        Rect r, Color stroke, double thickness, bool dashed = false)
    {
        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = r.Width,
            Height = r.Height,
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = thickness,
            Fill = Brushes.Transparent,
        };
        if (dashed) shape.StrokeDashArray = [4, 2];
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        return shape;
    }

    public void SetFrozenBackground(BitmapSource? img)
    {
        FrozenLayer.Source = img;
        FrozenLayer.Visibility = img is null ? Visibility.Collapsed : Visibility.Visible;
        // TextLayer items are in logical DIPs; FrozenLayer (Stretch="Fill") scales the
        // background image to fill the window, so no transform on TextLayer is needed.
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

    private void BtnAddRegion_Click(object sender, RoutedEventArgs e) =>
        AddRegionRequested?.Invoke(this, EventArgs.Empty);

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
