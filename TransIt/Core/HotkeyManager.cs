using System.Windows.Interop;
using TransIt.Infrastructure;

namespace TransIt.Core;

public sealed class HotkeyManager : IDisposable
{
    public const int ID_SNAPSHOT = 1;   // Alt+2
    public const int ID_REGION   = 2;   // Ctrl+2
    public const int ID_REALTIME = 3;   // Alt+3

    public event EventHandler<int>? HotkeyPressed;

    private HwndSource? _source;
    private readonly List<int> _registeredIds = [];

    public void Initialize()
    {
        var p = new HwndSourceParameters("TransIt_HotkeyHost")
        {
            Width = 0, Height = 0,
            WindowStyle = 0,
            ParentWindow = IntPtr.Zero,
            UsesPerPixelOpacity = false
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(int id, uint modifiers, uint vk)
    {
        if (_source is null) throw new InvalidOperationException("Call Initialize first.");
        bool ok = NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
        if (ok) _registeredIds.Add(id);
        return ok;
    }

    public void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _registeredIds)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(this, wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.Dispose();
    }
}
