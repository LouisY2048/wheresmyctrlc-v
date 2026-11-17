using System.Text;

namespace WheresMyCtrlCV.Input;

public static class WindowTarget
{
    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static string GetTitle(nint window)
    {
        var title = new StringBuilder(1024);
        NativeMethods.GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    public static bool IsOwnProcessWindow(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    public static bool IsValid(nint window) => window != 0 && NativeMethods.IsWindow(window);

    public static bool AreModifiersPressed() =>
        IsPressed(0x10) || IsPressed(0x11) || IsPressed(0x12) || IsPressed(0x5B) || IsPressed(0x5C);

    public static bool IsCapsLockOn() => (NativeMethods.GetKeyState(0x14) & 1) != 0;

    internal static bool IsPressed(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
