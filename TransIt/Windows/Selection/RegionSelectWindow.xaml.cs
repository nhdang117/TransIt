using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class RegionSelectWindow : Window
{
    public System.Drawing.Rectangle? SelectedRect { get; private set; }
    public bool Cancelled { get; private set; }

    private System.Windows.Point _startPoint;
    private bool _isDragging;

    public RegionSelectWindow()
    {
        InitializeComponent();
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(SelectionCanvas);
        _isDragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        Mouse.Capture(SelectionCanvas);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var cur = e.GetPosition(SelectionCanvas);
        double x = Math.Min(_startPoint.X, cur.X);
        double y = Math.Min(_startPoint.Y, cur.Y);
        double w = Math.Abs(cur.X - _startPoint.X);
        double h = Math.Abs(cur.Y - _startPoint.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionRect, x);
        System.Windows.Controls.Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width  = w;
        SelectionRect.Height = h;

        SizeLabel.Text = $"{(int)w} × {(int)h}";
        System.Windows.Controls.Canvas.SetLeft(SizeLabel, x);
        System.Windows.Controls.Canvas.SetTop(SizeLabel, Math.Max(0, y - 24));
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        Mouse.Capture(null);
        _isDragging = false;

        double dpiScale = GetDpiScale();
        int rx = (int)(System.Windows.Controls.Canvas.GetLeft(SelectionRect) * dpiScale);
        int ry = (int)(System.Windows.Controls.Canvas.GetTop(SelectionRect)  * dpiScale);
        int rw = (int)(SelectionRect.Width  * dpiScale);
        int rh = (int)(SelectionRect.Height * dpiScale);

        if (rw > 4 && rh > 4)
        {
            // Offset by virtual screen origin (physical coords)
            int ox = (int)(SystemParameters.VirtualScreenLeft * dpiScale);
            int oy = (int)(SystemParameters.VirtualScreenTop  * dpiScale);
            SelectedRect = new System.Drawing.Rectangle(rx + ox, ry + oy, rw, rh);
        }

        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled = true;
            Mouse.Capture(null);
            Close();
        }
    }

    private double GetDpiScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return DpiHelper.GetDpiScaleForHwnd(hwnd);
    }
}
