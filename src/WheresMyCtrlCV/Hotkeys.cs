using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WheresMyCtrlCV;

internal sealed class Hotkeys : IDisposable
{
    private const int OpenId = 0x4356;
    private const int StopId = 0x4357;
    private readonly nint _handle;
    private readonly HwndSource _source;
    public event Action? OpenRequested;
    public event Action? StopRequested;
    public bool OpenRegistered { get; }
    private bool _stopRegistered;

    public Hotkeys(nint handle)
    {
        _handle = handle;
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(WndProc);
        OpenRegistered = RegisterHotKey(handle, OpenId, 0x4000 | 0x0001 | 0x0002, 0x56);
    }

    public bool RegisterStop() => _stopRegistered || (_stopRegistered = RegisterHotKey(_handle, StopId, 0x4000, 0x77));

    public void UnregisterStop()
    {
        if (_stopRegistered) UnregisterHotKey(_handle, StopId);
        _stopRegistered = false;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312) return 0;
        if ((int)wParam == OpenId) { OpenRequested?.Invoke(); handled = true; }
        if ((int)wParam == StopId) { StopRequested?.Invoke(); handled = true; }
        return 0;
    }

    public void Dispose()
    {
        UnregisterStop();
        if (OpenRegistered) UnregisterHotKey(_handle, OpenId);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
