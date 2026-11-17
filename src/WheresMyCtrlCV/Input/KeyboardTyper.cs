using System.Runtime.InteropServices;

namespace WheresMyCtrlCV.Input;

public sealed class KeyboardTyper
{
    private readonly IKeyboardBackend backend;

    public KeyboardTyper() : this(new WindowsKeyboardBackend()) { }

    internal KeyboardTyper(IKeyboardBackend backend) => this.backend = backend;

    public static string? Validate(string text, TypingOptions options)
    {
        InputPlanner.TryBuild(text, options, out _, out string? error);
        return error;
    }

    public async Task TypeAsync(string text, nint targetWindow, TypingOptions options,
        IProgress<TypingProgress>? progress, CancellationToken cancellationToken)
    {
        // Build and validate the ENTIRE command before sending any event.
        if (!InputPlanner.TryBuild(text, options, out var plan, out string? error))
            throw new ArgumentException(error, nameof(text));

        CheckTarget(targetWindow, options.Mode, cancellationToken);
        progress?.Report(new(0, plan.Count));
        for (int index = 0; index < plan.Count; index++)
        {
            CheckTarget(targetWindow, options.Mode, cancellationToken);
            SendCharacter(plan[index]);
            progress?.Report(new(index + 1, plan.Count));
            if (index + 1 < plan.Count)
            {
                // Short slices allow F8 polling to work even if a target captures hotkeys.
                int remaining = options.CharacterDelayMs;
                while (remaining > 0)
                {
                    int slice = Math.Min(remaining, 20);
                    await Task.Delay(slice, cancellationToken);
                    CheckTarget(targetWindow, options.Mode, cancellationToken);
                    remaining -= slice;
                }
            }
        }
    }

    private void CheckTarget(nint targetWindow, TypingMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (backend.IsStopPressed)
            throw new OperationCanceledException("已按 F8 停止输入。", cancellationToken);
        if (!backend.IsValid(targetWindow) || backend.IsOwnWindow(targetWindow))
            throw new InvalidOperationException("目标窗口不可用。请重新选择控制台窗口。 ");
        if (backend.ForegroundWindow != targetWindow)
            throw new InvalidOperationException("焦点已离开目标窗口，输入已停止。请检查已输入的内容后再重试。 ");
        if (backend.AreModifiersPressed)
            throw new InvalidOperationException("检测到 Ctrl、Alt、Shift 或 Win 正被按下，输入已停止。请松开这些按键后重试。 ");
        if (mode == TypingMode.ScanCode && backend.IsCapsLockOn)
            throw new InvalidOperationException("请先关闭 Caps Lock，并将本地和远端键盘设为英文（美式）后重试。 ");
    }

    private void SendCharacter(KeyboardEvent[] events)
    {
        uint sent = backend.Send(events);
        if (sent == events.Length)
            return;

        int error = backend.LastError;
        // A partial native send may have left a key/modifier down. Release only
        // the keys whose downs were accepted, never retry the command's text.
        var held = new List<KeyboardEvent>();
        for (int i = 0; i < Math.Min(sent, (uint)events.Length); i++)
        {
            KeyboardEvent item = events[i];
            if (!item.IsKeyUp)
                held.Add(item);
            else
                held.RemoveAll(down => down.ScanCode == item.ScanCode && down.Flags == (item.Flags & ~KeyboardEvent.KeyUp));
        }
        if (held.Count > 0)
            backend.Send(held.AsEnumerable().Reverse().Select(item => item.AsKeyUp()).ToArray());

        throw new InvalidOperationException($"Windows 未能发送全部按键（{sent}/{events.Length}，错误码 {error}）。目标窗口可能以管理员身份运行，或禁止模拟按键。请检查已输入内容；如目标已提权，请以相同权限运行本工具。 ");
    }
}

internal interface IKeyboardBackend
{
    nint ForegroundWindow { get; }
    bool IsValid(nint window);
    bool IsOwnWindow(nint window);
    bool AreModifiersPressed { get; }
    bool IsCapsLockOn { get; }
    bool IsStopPressed { get; }
    int LastError { get; }
    uint Send(KeyboardEvent[] events);
}

internal sealed class WindowsKeyboardBackend : IKeyboardBackend
{
    public nint ForegroundWindow => WindowTarget.GetForegroundWindow();
    public bool IsValid(nint window) => WindowTarget.IsValid(window);
    public bool IsOwnWindow(nint window) => WindowTarget.IsOwnProcessWindow(window);
    public bool AreModifiersPressed => WindowTarget.AreModifiersPressed();
    // GetKeyState's toggle state follows the calling thread's message queue.
    // Also stop while Caps Lock is physically held, before a background queue
    // could observe its updated toggle state.
    public bool IsCapsLockOn => WindowTarget.IsCapsLockOn() || WindowTarget.IsPressed(0x14);
    public bool IsStopPressed => WindowTarget.IsPressed(0x77); // VK_F8
    public int LastError { get; private set; }

    public uint Send(KeyboardEvent[] events)
    {
        NativeMethods.Input[] inputs = events.Select(item => new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput { ScanCode = item.ScanCode, Flags = item.Flags }
            }
        }).ToArray();
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        LastError = Marshal.GetLastWin32Error();
        return sent;
    }
}
