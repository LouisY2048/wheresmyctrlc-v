using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using WheresMyCtrlCV.Input;

namespace WindowsSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--sink", var pipeName])
        {
            // Disable IME on the disposable sink thread before creating any HWND.
            // ImeMode.Disable on individual controls is insufficient with some TSF IMEs.
            Native.ImmDisableIME(0);
            ApplicationConfiguration.Initialize();
            Application.Run(new SinkForm(pipeName));
            return 0;
        }
        if (args is not ["--run"])
        {
            Console.WriteLine("Native keyboard smoke checks (opens two harmless text windows).\n" +
                "Run explicitly: dotnet run --project tests/WindowsSmoke -- --run\n" +
                "Keep keyboard/mouse idle while running. Caps Lock must be off.\n" +
                "Only this harness's child windows receive input. No terminals are used.");
            return 0;
        }
        try { RunChecksAsync().GetAwaiter().GetResult(); return 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
    }

    private static async Task RunChecksAsync()
    {
        if (WindowTarget.AreModifiersPressed() || WindowTarget.IsCapsLockOn())
            throw new InvalidOperationException("Release Ctrl/Shift/Alt/Win and turn off Caps Lock before running.");

        await using var sink = await SinkClient.StartAsync();
        var typer = new KeyboardTyper();
        var scan = new TypingOptions(TypingMode.ScanCode, 30, false, false);
        int checks = 0;
        async Task Check(string name, Func<Task> action)
        {
            await sink.ResetAndFocusAsync();
            await action();
            Assert(!WindowTarget.AreModifiersPressed(), "An injected modifier is still held.");
            Console.WriteLine($"PASS: {name}");
            checks++;
        }

        await Check("all 95 printable ASCII characters, surrounding spaces, no implicit Enter", async () =>
        {
            string text = "  " + string.Concat(Enumerable.Range(32, 95).Select(value => (char)value)) + "  ";
            var progress = new List<TypingProgress>();
            await typer.TypeAsync(text, sink.WindowA, scan, new InlineProgress(progress.Add), CancellationToken.None);
            await sink.ExpectAsync(text, 0);
            Assert(progress[^1] == new TypingProgress(text.Length, text.Length), "Progress did not reach the exact total.");
        });

        await Check("Unicode Chinese and surrogate pair", async () =>
        {
            const string text = "  中文 测试 🙂 café  ";
            await typer.TypeAsync(text, sink.WindowA, scan with { Mode = TypingMode.Unicode }, null, CancellationToken.None);
            await sink.ExpectAsync(text, 0);
        });

        await Check("explicit multiline normalizes CRLF, CR, and LF to one Enter each", async () =>
        {
            await typer.TypeAsync("Line A\r\nLine B\rLine C\n", sink.WindowA,
                scan with { AllowLineBreaks = true }, null, CancellationToken.None);
            await sink.ExpectAsync("Line A\r\nLine B\r\nLine C\r\n", 3);
        });

        await Check("explicit appended Enter happens exactly once", async () =>
        {
            await typer.TypeAsync("Finished", sink.WindowA, scan with { AppendEnter = true }, null, CancellationToken.None);
            await sink.ExpectAsync("Finished\r\n", 1);
        });

        await Check("unsupported final character rejects entire input", async () =>
        {
            await ExpectThrowsAsync<ArgumentException>(() => typer.TypeAsync("HarmlessPrefix中文", sink.WindowA,
                scan, null, CancellationToken.None));
            await sink.ExpectAsync("", 0);
        });

        await Check("trailing copied newline cannot execute implicitly", async () =>
        {
            await ExpectThrowsAsync<ArgumentException>(() => typer.TypeAsync("HarmlessText\r\n", sink.WindowA,
                scan, null, CancellationToken.None));
            await sink.ExpectAsync("", 0);
        });

        await Check("cancellation stops at a complete character and releases Shift", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value.Completed == 3) cancellation.Cancel(); });
            await ExpectThrowsAsync<OperationCanceledException>(() => typer.TypeAsync("A!B?C", sink.WindowA,
                scan with { CharacterDelayMs = 200, AppendEnter = true }, progress, cancellation.Token));
            await sink.ExpectAsync("A!B", 0);
        });

        await Check("focus loss stops before sending to second harmless window", async () =>
        {
            bool switched = false;
            var progress = new InlineProgress(value =>
            {
                if (value.Completed != 3) return;
                sink.FocusSecondWindow();
                switched = true;
            });
            await ExpectThrowsAsync<InvalidOperationException>(() => typer.TypeAsync("FirstSecond", sink.WindowA,
                scan with { CharacterDelayMs = 100, AppendEnter = true }, progress, CancellationToken.None));
            Assert(switched && WindowTarget.GetForegroundWindow() == sink.WindowB, "Test did not switch to the second sink.");
            await sink.ExpectAsync("Fir", 0);
        });
        Console.WriteLine($"All {checks} Windows native-input smoke checks passed.");
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, but input succeeded.");
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress(Action<TypingProgress> report) : IProgress<TypingProgress>
    {
        public void Report(TypingProgress value) => report(value);
    }
}

internal sealed record Snapshot(long WindowA, long WindowB, string TextA, int EnterA, string TextB, int EnterB);

internal sealed class SinkClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    public nint WindowA { get; private set; }
    public nint WindowB { get; private set; }

    private SinkClient(Process process, NamedPipeServerStream pipe)
    {
        this.process = process;
        this.pipe = pipe;
        reader = new StreamReader(pipe, leaveOpen: true);
        writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<SinkClient> StartAsync()
    {
        string name = $"WheresMyCtrlCV-smoke-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--sink");
        start.ArgumentList.Add(name);
        Process child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start harmless input sink.");
        SinkClient? client = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token);
            // AutoFlush writes the encoder preamble, so do not create the writer
            // until the server pipe has actually connected to its child.
            client = new SinkClient(child, pipe);
            Snapshot first = await client.ReadAsync();
            client.WindowA = (nint)first.WindowA;
            client.WindowB = (nint)first.WindowB;
            client.VerifyChildWindow(client.WindowA);
            client.VerifyChildWindow(client.WindowB);
            return client;
        }
        catch
        {
            if (client is not null) await client.DisposeAsync();
            else
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
                try { child.Dispose(); } catch { }
                try { await pipe.DisposeAsync(); } catch { }
            }
            throw;
        }
    }

    public async Task ResetAndFocusAsync()
    {
        await RequestAsync("reset");
        Focus(WindowA);
        // Give the target's activation/focus messages time to run before sending any key.
        await Task.Delay(180);
        Program.Assert(WindowTarget.GetForegroundWindow() == WindowA, "Foreground activation was lost before the check.");
    }

    public void Focus(nint window)
    {
        VerifyChildWindow(window);
        if (!Native.SetForegroundWindow(window) || WindowTarget.GetForegroundWindow() != window)
            throw new InvalidOperationException("Windows refused to focus the known test window. No keys were sent to another window.");
    }

    public void FocusSecondWindow()
    {
        VerifyChildWindow(WindowB);
        // The child already owns the foreground window and can switch its own
        // windows without relying on foreground rights of this background runner.
        RequestAsync("focusB").GetAwaiter().GetResult();
        Program.Assert(WindowTarget.GetForegroundWindow() == WindowB, "The child could not activate its second known window.");
    }

    private void VerifyChildWindow(nint window)
    {
        Native.GetWindowThreadProcessId(window, out uint owner);
        Program.Assert(!process.HasExited && owner == (uint)process.Id && WindowTarget.IsValid(window),
            "Refusing to target a window that is not owned by this harness's live child process.");
    }

    public async Task ExpectAsync(string expectedText, int expectedEnters)
    {
        Snapshot? last = null;
        var timeout = Stopwatch.StartNew();
        do
        {
            await Task.Delay(80);
            last = await RequestAsync("snapshot");
            if (last.TextA == expectedText && last.EnterA == expectedEnters && last.TextB == "" && last.EnterB == 0)
                return;
        } while (timeout.Elapsed < TimeSpan.FromSeconds(2));
        throw new InvalidOperationException($"Sink mismatch. Expected {JsonSerializer.Serialize(expectedText)} / Enter={expectedEnters}; " +
            $"received {JsonSerializer.Serialize(last)}.");
    }

    private async Task<Snapshot> RequestAsync(string command)
    {
        await writer.WriteLineAsync(command);
        return await ReadAsync();
    }

    private async Task<Snapshot> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Sink disconnected.");
        return JsonSerializer.Deserialize<Snapshot>(line) ?? throw new IOException("Sink returned invalid JSON.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (pipe.IsConnected && !process.HasExited) await writer.WriteLineAsync("close");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            // Only terminate the dedicated child we created; never any external application.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        // Cleanup must not hide the original assertion or connection failure.
        try { reader.Dispose(); } catch { }
        try { writer.Dispose(); } catch { }
        try { await pipe.DisposeAsync(); } catch { }
        try { process.Dispose(); } catch { }
    }
}

internal sealed class SinkForm : Form
{
    private readonly string pipeName;
    private readonly TextBox textA = MakeTextBox();
    private readonly TextBox textB = MakeTextBox();
    private readonly Form second = new();
    private int entersA;
    private int entersB;

    public SinkForm(string pipeName)
    {
        this.pipeName = pipeName;
        Text = "CtrlCV smoke check A — harmless text only";
        Size = new Size(580, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(textA);
        textA.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) entersA++; };
        textB.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) entersB++; };
        Activated += (_, _) => textA.Focus();
        second.Text = "CtrlCV smoke check B — must stay empty";
        second.Size = new Size(580, 280);
        second.StartPosition = FormStartPosition.Manual;
        second.Controls.Add(textB);
        second.Activated += (_, _) => textB.Focus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Set the test thread's own input layout, leaving the user's other applications untouched.
        var us = InputLanguage.InstalledInputLanguages.Cast<InputLanguage>()
            .FirstOrDefault(language => language.Culture.Name == "en-US");
        if (us is not null) InputLanguage.CurrentInputLanguage = us;
        second.Location = new Point(Left + 70, Top + 70);
        second.Show();
        Activate();
        textA.Focus();
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(CaptureSnapshot()));
            while (await reader.ReadLineAsync() is { } command)
            {
                if (command == "close") break;
                if (command == "reset")
                {
                    textA.Clear(); textB.Clear(); entersA = 0; entersB = 0;
                }
                else if (command == "focusB")
                {
                    Native.SetForegroundWindow(second.Handle);
                    textB.Focus();
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(CaptureSnapshot()));
            }
        }
        catch (IOException) { /* Parent stopped: close these disposable test windows. */ }
        finally { second.Close(); Close(); }
    }

    private Snapshot CaptureSnapshot() => new(Handle.ToInt64(), second.Handle.ToInt64(), textA.Text, entersA, textB.Text, entersB);

    private static TextBox MakeTextBox() => new()
    {
        Multiline = true, AcceptsReturn = true, AcceptsTab = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 12), ScrollBars = ScrollBars.Both,
        WordWrap = false, ImeMode = ImeMode.Disable
    };
}

internal static class Native
{
    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmDisableIME(uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
