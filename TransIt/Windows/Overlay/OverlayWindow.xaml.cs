using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TransIt.Infrastructure;
using TransIt.Models;

namespace TransIt.Windows.Overlay;

public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _annotationMode;
    private IntPtr _kbHook = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _kbProcDelegate;
    private List<OverlayTextItem> _currentItems = [];

    public event EventHandler? EscapePressed;
    public event EventHandler? AddRegionRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        Canvas.CloseRequested     += (_, _) => HideOverlay();
        Canvas.AddRegionRequested += (_, _) => AddRegionRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetClickThrough(true);
    }

    /// Cover the full virtual screen (all monitors).
    private void PositionOnVirtualScreen()
    {
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public void ShowOverlay(IList<OverlayTextItem> items)
    {
        PositionOnVirtualScreen();
        Canvas.SetFrozenBackground(null);
        Canvas.RenderItems(items);
        Canvas.ShowAnnotationToolbar(false);
        _annotationMode = false;
        SetClickThrough(true);
        Show();
    }

    public void ShowFrozenOverlay(IList<OverlayTextItem> items, BitmapSource background)
    {
        PositionOnVirtualScreen();
        Canvas.SetFrozenBackground(background);
        Canvas.RenderItems(items);
        Canvas.ShowAnnotationToolbar(false);
        _annotationMode = false;
        SetClickThrough(false);
        Show();
        Activate();
        InstallEscHook();
    }

    /// Ctrl+1 test mode: shows raw OCR line boxes (yellow), combined
    /// paragraph blocks (red), and detected layout regions (cyan dashed) over a
    /// frozen screenshot, no translation involved.
    public void ShowDebugOverlay(IList<Rect> lineRects, IList<Rect> blockRects, BitmapSource background,
                                  IList<Rect>? regionRects = null, IList<Rect>? mergeZoneRects = null)
    {
        PositionOnVirtualScreen();
        Canvas.SetFrozenBackground(background);
        Canvas.ShowLoading(false);
        Canvas.RenderItems([]);
        Canvas.RenderDebugRects(lineRects, blockRects, regionRects, mergeZoneRects);
        Canvas.ShowAnnotationToolbar(false);
        _annotationMode = false;
        SetClickThrough(false);
        Show();
        Activate();
        InstallEscHook();
    }

    public void ShowLoadingOverlay(BitmapSource background)
    {
        PositionOnVirtualScreen();
        Canvas.SetFrozenBackground(background);
        Canvas.RenderItems([]);
        Canvas.ShowAnnotationToolbar(false);
        Canvas.ShowLoading(true);
        _annotationMode = false;
        SetClickThrough(false);
        Show();
        Activate();
        InstallEscHook();
    }

    public void UpdateLoadingStatus(string text) => Canvas.UpdateLoadingStatus(text);

    public void UpdateWithTranslation(IList<OverlayTextItem> items)
    {
        _currentItems = [..items];
        Canvas.ShowLoading(false);
        Canvas.RenderItems(_currentItems);
        EnterAnnotationMode();
    }

    public void UpdateWithTranslationAndDebug(IList<OverlayTextItem> items,
        IList<Rect> lineRects, IList<Rect> blockRects,
        IList<Rect>? regionRects = null, IList<Rect>? mergeZoneRects = null)
    {
        _currentItems = [..items];
        Canvas.ShowLoading(false);
        Canvas.RenderItems(_currentItems);
        Canvas.RenderDebugRects(lineRects, blockRects, regionRects, mergeZoneRects);
        EnterAnnotationMode();
    }

    public void HideOverlay()
    {
        _currentItems.Clear();
        UninstallEscHook();
        Canvas.ShowLoading(false);
        Canvas.SetFrozenBackground(null);
        Canvas.RenderDebugRects([], []);
        Hide();
        Canvas.ClearInk();
        EscapePressed?.Invoke(this, EventArgs.Empty);
    }

    public void AddTranslationItems(IList<OverlayTextItem> newItems)
    {
        _currentItems.AddRange(newItems);
        Canvas.RenderItems(_currentItems);
    }

    private void InstallEscHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProcDelegate = EscHookProc;
        _kbHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _kbProcDelegate,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    private void UninstallEscHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProcDelegate = null;
    }

    private IntPtr EscHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_KEYDOWN)
        {
            var ks = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (ks.vkCode == NativeMethods.VK_ESCAPE)
            {
                Dispatcher.BeginInvoke(HideOverlay);
                return new IntPtr(1); // swallow the key
            }
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    public void EnterAnnotationMode()
    {
        _annotationMode = true;
        SetClickThrough(false);
        Canvas.ShowAnnotationToolbar(true);
    }

    public void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        if (enabled)
            style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
        else
            style &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            EnterAnnotationMode();
            e.Handled = true;
        }
    }

    protected override void OnMouseDoubleClick(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // Double-click toggles annotation mode
        if (!_annotationMode)
            EnterAnnotationMode();
    }
}
