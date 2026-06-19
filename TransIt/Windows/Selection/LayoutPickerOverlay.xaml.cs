using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TransIt.Infrastructure;
using TransIt.Models;

namespace TransIt.Windows.Selection;

public partial class LayoutPickerOverlay : Window
{
    public System.Drawing.Rectangle? SelectedPhysRect { get; private set; }
    public LayoutCategory?           SelectedCategory  { get; private set; }
    public bool                      Cancelled         { get; private set; }

    private readonly BitmapSource              _bitmapSource;
    private readonly Bitmap                    _bitmap;
    private readonly System.Drawing.Rectangle  _monPhysRect;
    private readonly double                    _dpiScale;
    private readonly Task<List<LayoutRegion>>  _layoutTask;
    private readonly CancellationTokenSource   _layoutCts;

    private List<LayoutRegion> _layoutRegions = [];
    private int  _hoveredIndex = -1;
    private bool _mouseDown;
    private bool _isDragging;
    private bool _closed;
    private System.Windows.Point _dragStart;
    private const double DragThreshold = 5;

    public LayoutPickerOverlay(BitmapSource bitmapSource, Bitmap bitmap,
                                System.Drawing.Rectangle monPhysRect, double dpiScale,
                                Task<List<LayoutRegion>> layoutTask,
                                CancellationTokenSource layoutCts)
    {
        InitializeComponent();
        _bitmapSource = bitmapSource;
        _bitmap       = bitmap;
        _monPhysRect  = monPhysRect;
        _dpiScale     = dpiScale;
        _layoutTask   = layoutTask;
        _layoutCts    = layoutCts;
        Loaded += OnLoaded;
        Closed += (_, _) => { _closed = true; _layoutCts.Cancel(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position window exactly over the captured monitor.
        Left   = _monPhysRect.Left / _dpiScale;
        Top    = _monPhysRect.Top  / _dpiScale;
        Width  = _monPhysRect.Width  / _dpiScale;
        Height = _monPhysRect.Height / _dpiScale;

        BackgroundImage.Source = _bitmapSource;

        // Hint bar: bottom-center.
        Canvas.SetLeft(HintBorder, (Width - 560) / 2);
        Canvas.SetTop(HintBorder,  Height - 44);

        Keyboard.Focus(this);

        // Start layout detection in background; results streamed to UI when ready.
        _ = RunYoloAsync();
    }

    private async Task RunYoloAsync()
    {
        try
        {
            var regions = await _layoutTask;
            if (_closed) return;
            Dispatcher.Invoke(() => OnLayoutDetected(regions));
        }
        catch (OperationCanceledException) { }
        catch { /* detection failure is non-fatal; user can still drag-select */ }
    }

    private void OnLayoutDetected(List<LayoutRegion> regions)
    {
        _layoutRegions = regions;

        // Draw thin coloured borders for every detected region.
        foreach (var region in regions)
        {
            var cr   = ToCanvasRect(region.BoundingRect);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width           = cr.Width,
                Height          = cr.Height,
                Stroke          = GetCategoryBrush(region.Category),
                StrokeThickness = 1.5,
                Fill            = System.Windows.Media.Brushes.Transparent,
                Opacity         = 0.55,
            };
            Canvas.SetLeft(rect, cr.X);
            Canvas.SetTop(rect,  cr.Y);
            RegionsCanvas.Children.Add(rect);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_mouseDown)
        {
            double dx = pos.X - _dragStart.X;
            double dy = pos.Y - _dragStart.Y;

            if (!_isDragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
                _isDragging = true;

            if (_isDragging)
            {
                double l = Math.Min(_dragStart.X, pos.X);
                double t = Math.Min(_dragStart.Y, pos.Y);
                Canvas.SetLeft(DragRect, l);
                Canvas.SetTop(DragRect,  t);
                DragRect.Width      = Math.Abs(dx);
                DragRect.Height     = Math.Abs(dy);
                DragRect.Visibility = Visibility.Visible;
                HoverRect.Visibility       = Visibility.Collapsed;
                CategoryBadge.Visibility   = Visibility.Collapsed;
            }
            return;
        }

        // Hover: find the innermost detected region containing the cursor.
        int found   = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _layoutRegions.Count; i++)
        {
            var cr = ToCanvasRect(_layoutRegions[i].BoundingRect);
            if (cr.Contains(pos))
            {
                double area = cr.Width * cr.Height;
                if (area < best) { best = area; found = i; }
            }
        }

        if (found == _hoveredIndex) return;
        _hoveredIndex = found;

        if (found >= 0)
        {
            var cr = ToCanvasRect(_layoutRegions[found].BoundingRect);
            Canvas.SetLeft(HoverRect, cr.X);
            Canvas.SetTop(HoverRect,  cr.Y);
            HoverRect.Width      = cr.Width;
            HoverRect.Height     = cr.Height;
            HoverRect.Visibility = Visibility.Visible;

            CategoryText.Text    = _layoutRegions[found].Category.ToString();
            Canvas.SetLeft(CategoryBadge, cr.X + 4);
            Canvas.SetTop(CategoryBadge,  Math.Max(0, cr.Y - 24));
            CategoryBadge.Visibility = Visibility.Visible;
        }
        else
        {
            HoverRect.Visibility     = Visibility.Collapsed;
            CategoryBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown  = true;
        _isDragging = false;
        _dragStart  = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _mouseDown = false;

        if (_isDragging)
        {
            _isDragging         = false;
            DragRect.Visibility = Visibility.Collapsed;

            var pos    = e.GetPosition(this);
            double l   = Math.Min(_dragStart.X, pos.X);
            double t   = Math.Min(_dragStart.Y, pos.Y);
            double w   = Math.Abs(pos.X - _dragStart.X);
            double h   = Math.Abs(pos.Y - _dragStart.Y);

            if (w < 5 || h < 5) { Cancelled = true; Close(); return; }

            SelectedPhysRect = ToPhysRect(l, t, w, h);
            SelectedCategory = null; // caller will run YOLO on selection to determine type
        }
        else
        {
            if (_hoveredIndex < 0) { Cancelled = true; Close(); return; }

            var region       = _layoutRegions[_hoveredIndex];
            SelectedPhysRect = new System.Drawing.Rectangle(
                (int)region.BoundingRect.X + _monPhysRect.Left,
                (int)region.BoundingRect.Y + _monPhysRect.Top,
                (int)region.BoundingRect.Width,
                (int)region.BoundingRect.Height);
            SelectedCategory = region.Category;
        }

        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancelled = true; Close(); }
    }

    // Converts a bitmap-relative physical-pixel rect to canvas DIP coords.
    private System.Windows.Rect ToCanvasRect(System.Windows.Rect physBitmapRect) =>
        new(physBitmapRect.X / _dpiScale, physBitmapRect.Y / _dpiScale,
            physBitmapRect.Width / _dpiScale, physBitmapRect.Height / _dpiScale);

    // Converts canvas DIP coords (window-relative) to global physical-pixel rect.
    private System.Drawing.Rectangle ToPhysRect(double canvasX, double canvasY, double canvasW, double canvasH)
    {
        // window.Left = monPhysRect.Left / dpiScale, so:
        // globalPhysX = (canvasX + window.Left) * dpiScale = canvasX*dpiScale + monPhysRect.Left
        int px = (int)(canvasX * _dpiScale) + _monPhysRect.Left;
        int py = (int)(canvasY * _dpiScale) + _monPhysRect.Top;
        int pw = (int)(canvasW * _dpiScale);
        int ph = (int)(canvasH * _dpiScale);
        return new System.Drawing.Rectangle(px, py, pw, ph);
    }

    private static System.Windows.Media.SolidColorBrush GetCategoryBrush(LayoutCategory cat) =>
        cat switch
        {
            LayoutCategory.Table  => System.Windows.Media.Brushes.Orange,
            LayoutCategory.Title  => System.Windows.Media.Brushes.Violet,
            LayoutCategory.Figure => System.Windows.Media.Brushes.LimeGreen,
            LayoutCategory.List   => System.Windows.Media.Brushes.Cyan,
            _                     => System.Windows.Media.Brushes.DeepSkyBlue,
        };
}
