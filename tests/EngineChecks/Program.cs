using System.Runtime.InteropServices;
using WheresMyCtrlCV.Input;

int passed = 0;
var scan = new TypingOptions(TypingMode.ScanCode, 1, false, false);
var unicode = scan with { Mode = TypingMode.Unicode };

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    passed++;
    Console.WriteLine($"PASS: {description}");
}

List<KeyboardEvent[]> Plan(string text, TypingOptions? options = null)
{
    if (!InputPlanner.TryBuild(text, options ?? scan, out var plan, out var error))
        throw new Exception(error);
    return plan;
}

async Task ExpectFailure<TException>(string text, FakeBackend backend, TypingOptions? options = null,
    IProgress<TypingProgress>? progress = null, CancellationToken token = default) where TException : Exception
{
    try
    {
        await new KeyboardTyper(backend).TypeAsync(text, 123, options ?? scan, progress, token);
        throw new Exception($"Expected {typeof(TException).Name}");
    }
    catch (TException) { }
}

Check(Marshal.SizeOf<NativeMethods.Input>() == (IntPtr.Size == 8 ? 40 : 28), "Native INPUT uses the correct architecture-dependent size");
var allAscii = Plan(new string(Enumerable.Range(32, 95).Select(value => (char)value).ToArray()));
Check(allAscii.Count == 95, "All 95 printable US ASCII characters are supported");
Check(allAscii.Select(events => string.Join(",", events.Select(e => $"{e.ScanCode}:{e.Flags}"))).Distinct().Count() == 95,
    "Every printable ASCII character has a distinct scan sequence");
Check(allAscii.All(events => events.All(e => (e.Flags & KeyboardEvent.Scan) != 0)), "Remote mode uses physical scan events exclusively");
Check(Plan("A")[0].SequenceEqual(new KeyboardEvent[] {
    new(0x2A, 8), new(0x1E, 8), new(0x1E, 10), new(0x2A, 10) }), "Uppercase is one balanced Shift + key batch");
Check(Plan("|\\").SelectMany(events => events).Count(e => e.ScanCode == 0x2B) == 4,
    "Pipe and backslash use the US backslash key");
Check(Plan("  ls -la  ").Count == 10, "Leading and trailing whitespace is preserved");
Check(Plan("ls").All(events => events.All(e => e.ScanCode != 0x1C)), "Default command never appends Enter");
Check(Plan("ls", scan with { AppendEnter = true }).Last()[0].ScanCode == 0x1C, "Explicit execution appends Enter");
Check(Plan("a\r\nb\rc\n", scan with { AllowLineBreaks = true }).Count == 6, "CRLF, CR and LF normalize to one Enter each");
Check(Plan("a\r\nb\rc\n", scan with { AllowLineBreaks = true }).Count(events => events[0].ScanCode == 0x1C) == 3,
    "Normalized multiline input sends the exact number of Enter keys");
Check(KeyboardTyper.Validate("ls\n", scan) is not null, "Trailing newline is rejected by default");
Check(KeyboardTyper.Validate("ls\r", scan) is not null, "Trailing carriage return is rejected by default");
Check(KeyboardTyper.Validate("", scan) is not null, "Empty input is rejected");
Check(KeyboardTyper.Validate("x", scan with { CharacterDelayMs = 0 }) is not null, "Invalid pacing is rejected");
Check(KeyboardTyper.Validate("x", scan with { Mode = (TypingMode)99 }) is not null, "Unknown mode is rejected");
Check(KeyboardTyper.Validate("echo 中文", scan) is not null, "Remote mode rejects unsupported text before sending");
Check(KeyboardTyper.Validate("echo\ttext", scan) is not null, "Tab is rejected to avoid accidental shell completion");
Check(KeyboardTyper.Validate("echo\u001b", unicode) is not null, "Unicode mode rejects Escape control characters");
Check(KeyboardTyper.Validate("a\u2028b", unicode) is not null, "Ambiguous Unicode line separator is rejected");
Check(KeyboardTyper.Validate("\ud800", unicode) is not null && KeyboardTyper.Validate("\udc00", unicode) is not null,
    "Malformed UTF-16 is rejected");
Check(Plan("中文", unicode).Count == 2, "Unicode mode accepts Chinese characters");
var emoji = Plan("😀", unicode);
Check(emoji.Count == 1 && emoji[0].Length == 4 && emoji[0][0].ScanCode == 0xD83D && emoji[0][2].ScanCode == 0xDE00,
    "Supplementary Unicode characters are emitted as a complete surrogate pair");

var invalidText = new FakeBackend();
await ExpectFailure<ArgumentException>("valid prefix\t", invalidText);
Check(invalidText.Sends.Count == 0, "Full validation prevents sending a valid prefix before a late invalid character");
var wrongFocus = new FakeBackend { ForegroundWindow = 456 };
await ExpectFailure<InvalidOperationException>("abc", wrongFocus);
Check(wrongFocus.Sends.Count == 0, "Wrong target focus prevents all injection");
var self = new FakeBackend { OwnWindow = true };
await ExpectFailure<InvalidOperationException>("abc", self);
Check(self.Sends.Count == 0, "Own process window cannot be a target");
var invalidWindow = new FakeBackend { ValidWindow = false };
await ExpectFailure<InvalidOperationException>("abc", invalidWindow);
Check(invalidWindow.Sends.Count == 0, "Destroyed target prevents injection");
var heldModifier = new FakeBackend { AreModifiersPressed = true };
await ExpectFailure<InvalidOperationException>("abc", heldModifier);
Check(heldModifier.Sends.Count == 0, "Held modifiers prevent accidental shortcuts");
var capsLock = new FakeBackend { IsCapsLockOn = true };
await ExpectFailure<InvalidOperationException>("abc", capsLock);
Check(capsLock.Sends.Count == 0, "Caps Lock blocks scan mode before incorrect casing can be typed");

var focusLost = new FakeBackend();
focusLost.AfterSend = () => focusLost.ForegroundWindow = 456;
await ExpectFailure<InvalidOperationException>("abc", focusLost);
Check(focusLost.Sends.Count == 1, "Focus loss after the first character stops subsequent characters");
var modifierDuringRun = new FakeBackend();
modifierDuringRun.AfterSend = () => modifierDuringRun.AreModifiersPressed = true;
await ExpectFailure<InvalidOperationException>("abc", modifierDuringRun);
Check(modifierDuringRun.Sends.Count == 1, "Modifier pressed mid-stream stops subsequent characters");
var capsDuringRun = new FakeBackend();
capsDuringRun.AfterSend = () => capsDuringRun.IsCapsLockOn = true;
await ExpectFailure<InvalidOperationException>("abc", capsDuringRun);
Check(capsDuringRun.Sends.Count == 1, "Caps Lock toggled mid-stream stops subsequent characters");
var stopped = new FakeBackend { IsStopPressed = true };
await ExpectFailure<OperationCanceledException>("abc", stopped);
Check(stopped.Sends.Count == 0, "F8 pressed before start cancels without injection");
var stopDuringRun = new FakeBackend();
stopDuringRun.AfterSend = () => stopDuringRun.IsStopPressed = true;
await ExpectFailure<OperationCanceledException>("abc", stopDuringRun);
Check(stopDuringRun.Sends.Count == 1, "F8 stops subsequent characters during a run");
using var cancellation = new CancellationTokenSource();
var cancelled = new FakeBackend();
cancelled.AfterSend = cancellation.Cancel;
await ExpectFailure<OperationCanceledException>("abc", cancelled, scan with { CharacterDelayMs = 2000 }, token: cancellation.Token);
Check(cancelled.Sends.Count == 1, "Cancellation interrupts even a long inter-character delay");
var partial = new FakeBackend { FirstSendCount = 2 };
await ExpectFailure<InvalidOperationException>("A", partial);
Check(partial.Sends.Count == 2 && partial.Sends[1].SequenceEqual(new KeyboardEvent[] { new(0x1E, 10), new(0x2A, 10) }),
    "Partial SendInput releases the accepted key and Shift downs without retyping text");
var zeroSent = new FakeBackend { FirstSendCount = 0 };
await ExpectFailure<InvalidOperationException>("A", zeroSent);
Check(zeroSent.Sends.Count == 1, "Zero accepted events do not trigger unnecessary key releases or retries");
var progressValues = new List<TypingProgress>();
var successful = new FakeBackend();
await new KeyboardTyper(successful).TypeAsync("中😀", 123, unicode, new ImmediateProgress(progressValues.Add), default);
Check(progressValues.SequenceEqual(new[] { new TypingProgress(0, 2), new TypingProgress(1, 2), new TypingProgress(2, 2) }),
    "Progress counts Unicode scalars and reaches exactly the planned total");
Check(successful.Sends.Count == 2, "Successful run emits precisely one batch per planned character");

Console.WriteLine($"\n{passed} engine checks passed. No real keystrokes were sent.");

sealed class ImmediateProgress(Action<TypingProgress> report) : IProgress<TypingProgress>
{
    public void Report(TypingProgress value) => report(value);
}

sealed class FakeBackend : IKeyboardBackend
{
    public nint ForegroundWindow { get; set; } = 123;
    public bool ValidWindow { get; set; } = true;
    public bool OwnWindow { get; set; }
    public bool AreModifiersPressed { get; set; }
    public bool IsCapsLockOn { get; set; }
    public bool IsStopPressed { get; set; }
    public int LastError => 5;
    public uint? FirstSendCount { get; init; }
    public Action? AfterSend { get; set; }
    public List<KeyboardEvent[]> Sends { get; } = [];
    public bool IsValid(nint window) => window == 123 && ValidWindow;
    public bool IsOwnWindow(nint window) => OwnWindow;
    public uint Send(KeyboardEvent[] events)
    {
        Sends.Add(events);
        AfterSend?.Invoke();
        return Sends.Count == 1 && FirstSendCount.HasValue ? FirstSendCount.Value : (uint)events.Length;
    }
}
