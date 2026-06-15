using TransIt.Infrastructure;

namespace TransIt.Core;

public sealed class WinEventHook : IDisposable
{
    public event EventHandler? AppSwitched;

    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.WinEventDelegate? _hookDelegate;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _hookDelegate = OnWinEvent;
        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookDelegate,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
            AppSwitched?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
