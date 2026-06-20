using System.Windows;
using System.Windows.Automation;
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

    private NativeMethods.RECT? _lastUiaRect;
    private DateTime _lastUiaQuery = DateTime.MinValue;
    private const int UiaThrottleMs = 80;
    private const double UiaMinArea = 15_000;   // ~123×122 px minimum panel

    private AutomationElement? _activeElement;  // UIA element currently highlighted
    private bool _isPinned;                     // scroll locked to an ancestor
    private NativeMethods.POINT _pinnedAt;      // cursor pos when lock started
    private const int PinResetPx = 30;          // physical px movement to unpin

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

        // If scroll-pinned, only unpin when cursor moves far enough
        if (_isPinned)
        {
            NativeMethods.GetCursorPos(out var curPt);
            int dx = curPt.X - _pinnedAt.X;
            int dy = curPt.Y - _pinnedAt.Y;
            if (dx * dx + dy * dy <= PinResetPx * PinResetPx)
                return;
            _isPinned = false;
        }

        // Hover mode: detect window under cursor
        NativeMethods.GetCursorPos(out var pt);

        // Set WS_EX_TRANSPARENT so both WindowFromPoint AND AutomationElement.FromPoint
        // skip this overlay and hit the real app underneath.
        var exStyle = NativeMethods.GetWindowLong(_myHwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_myHwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TRANSPARENT);
        var hwnd = NativeMethods.WindowFromPoint(pt);
        var uiaElement = TryGetUiaElementAtPoint(pt);       // call while still transparent
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
        _lastUiaRect = ResolveUiaPanelRect(uiaElement, rect);
        UpdateHighlight(_lastUiaRect ?? rect);
    }

    // Called while overlay has WS_EX_TRANSPARENT — UIA hit-test skips our window.
    private AutomationElement? TryGetUiaElementAtPoint(NativeMethods.POINT cursorPt)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastUiaQuery).TotalMilliseconds < UiaThrottleMs)
            return null;   // throttled; caller uses cached _lastUiaRect
        _lastUiaQuery = now;
        try { return AutomationElement.FromPoint(new Point(cursorPt.X, cursorPt.Y)); }
        catch { return null; }
    }

    private NativeMethods.RECT? ResolveUiaPanelRect(AutomationElement? element, NativeMethods.RECT windowRect)
    {
        if (element == null) return _lastUiaRect;   // throttled — keep last

        try
        {
            var walker  = TreeWalker.ControlViewWalker;
            var current = element;

            while (current != null)
            {
                var r = current.Current.BoundingRectangle;

                if (!r.IsEmpty && r.Width * r.Height >= UiaMinArea)
                {
                    bool isSubPanel =
                        r.Left   > windowRect.Left   + 10 ||
                        r.Top    > windowRect.Top    + 10 ||
                        r.Right  < windowRect.Right  - 10 ||
                        r.Bottom < windowRect.Bottom - 10;

                    if (isSubPanel)
                    {
                        _activeElement = current;
                        _lastUiaRect = new NativeMethods.RECT
                        {
                            Left   = (int)r.Left,
                            Top    = (int)r.Top,
                            Right  = (int)r.Right,
                            Bottom = (int)r.Bottom
                        };
                        return _lastUiaRect;
                    }

                    _activeElement = current; // full-window element — allow scroll-up from here
                    break;
                }

                current = walker.GetParent(current);
            }
        }
        catch { }

        _lastUiaRect = null;
        return null;
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

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta <= 0 || _activeElement == null) return; // only scroll up

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var parent = walker.GetParent(_activeElement);
            while (parent != null)
            {
                var r = parent.Current.BoundingRectangle;
                if (!r.IsEmpty && r.Width * r.Height >= UiaMinArea)
                {
                    _activeElement = parent;
                    _lastUiaRect = new NativeMethods.RECT
                    {
                        Left   = (int)r.Left,
                        Top    = (int)r.Top,
                        Right  = (int)r.Right,
                        Bottom = (int)r.Bottom
                    };
                    UpdateHighlight(_lastUiaRect.Value);
                    break;
                }
                parent = walker.GetParent(parent);
            }
        }
        catch { }

        NativeMethods.GetCursorPos(out _pinnedAt);
        _isPinned = true;
        e.Handled = true;
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
            if (_lastUiaRect.HasValue)
            {
                SelectedPhysRect = _lastUiaRect.Value.ToRectangle();
            }
            else
            {
                NativeMethods.GetWindowRect(_lastHwnd, out var rect);
                SelectedPhysRect = rect.ToRectangle();
            }
            SelectedDpiScale = _currentDpiScale;
        }
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancelled = true; Close(); }
    }
}
