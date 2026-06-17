using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class WindowPickerOverlay : Window
{
    public System.Drawing.Rectangle? SelectedPhysRect { get; private set; }
    public double SelectedDpiScale { get; private set; } = 1.0;
    public bool Cancelled { get; private set; }

    private IntPtr _myHwnd;
    private IntPtr _lastHwnd;
    private double _currentDpiScale = 1.0;

    private bool _mouseDown;
    private bool _isDragging;
    private Point _dragStartWpf;
    private const double DragThreshold = 5;

    public WindowPickerOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _myHwnd = new WindowInteropHelper(this).Handle;

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Canvas.SetLeft(HintBorder, (Width - 420) / 2);
        Canvas.SetTop(HintBorder,  Height - 52);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_mouseDown)
        {
            var cur = e.GetPosition(this);
            double dx = cur.X - _dragStartWpf.X;
            double dy = cur.Y - _dragStartWpf.Y;

            if (!_isDragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
                _isDragging = true;

            if (_isDragging)
            {
                double logL = Math.Min(_dragStartWpf.X, cur.X);
                double logT = Math.Min(_dragStartWpf.Y, cur.Y);
                Canvas.SetLeft(DragRect, logL);
                Canvas.SetTop(DragRect,  logT);
                DragRect.Width  = Math.Abs(dx);
                DragRect.Height = Math.Abs(dy);
                DragRect.Visibility  = Visibility.Visible;
                HighlightRect.Visibility = Visibility.Collapsed;
            }
            return;
        }

        // Hover mode: detect window under cursor
        NativeMethods.GetCursorPos(out var pt);

        var exStyle = NativeMethods.GetWindowLong(_myHwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_myHwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TRANSPARENT);
        var hwnd = NativeMethods.WindowFromPoint(pt);
        NativeMethods.SetWindowLong(_myHwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        if (hwnd == IntPtr.Zero || hwnd == _myHwnd) return;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        var target = root != IntPtr.Zero ? root : hwnd;
        if (hwnd != target)
        {
            NativeMethods.GetWindowRect(hwnd, out var cr);
            if ((cr.Right - cr.Left) * (cr.Bottom - cr.Top) > 40_000)
                target = hwnd;
        }

        _lastHwnd = target;
        double dpi = NativeMethods.GetDpiForWindow(target);
        _currentDpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

        NativeMethods.GetWindowRect(target, out var rect);
        UpdateHighlight(rect);
    }

    private void UpdateHighlight(NativeMethods.RECT physRect)
    {
        double logL = physRect.Left   / _currentDpiScale - Left;
        double logT = physRect.Top    / _currentDpiScale - Top;
        double logW = (physRect.Right  - physRect.Left) / _currentDpiScale;
        double logH = (physRect.Bottom - physRect.Top)  / _currentDpiScale;

        Canvas.SetLeft(HighlightRect, logL);
        Canvas.SetTop(HighlightRect,  logT);
        HighlightRect.Width  = Math.Max(0, logW);
        HighlightRect.Height = Math.Max(0, logH);
        HighlightRect.Visibility = Visibility.Visible;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown     = true;
        _isDragging    = false;
        _dragStartWpf  = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _mouseDown = false;

        if (_isDragging)
        {
            _isDragging = false;
            DragRect.Visibility = Visibility.Collapsed;

            var cur  = e.GetPosition(this);
            double logL = Math.Min(_dragStartWpf.X, cur.X);
            double logT = Math.Min(_dragStartWpf.Y, cur.Y);
            double logW = Math.Abs(cur.X - _dragStartWpf.X);
            double logH = Math.Abs(cur.Y - _dragStartWpf.Y);
            if (logW < 5 || logH < 5) { Cancelled = true; Close(); return; }

            double physL = (logL + Left) * _currentDpiScale;
            double physT = (logT + Top)  * _currentDpiScale;
            double physW = logW * _currentDpiScale;
            double physH = logH * _currentDpiScale;

            SelectedPhysRect = new System.Drawing.Rectangle(
                (int)physL, (int)physT, (int)physW, (int)physH);
            SelectedDpiScale = _currentDpiScale;
        }
        else
        {
            if (_lastHwnd == IntPtr.Zero) { Cancelled = true; Close(); return; }
            NativeMethods.GetWindowRect(_lastHwnd, out var rect);
            SelectedPhysRect = rect.ToRectangle();
            SelectedDpiScale = _currentDpiScale;
        }
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancelled = true; Close(); }
    }
}
