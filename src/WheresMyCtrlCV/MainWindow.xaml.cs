using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WheresMyCtrlCV.Input;

namespace WheresMyCtrlCV;

public partial class MainWindow : Window
{
    private const string AppTitle = "Where’s my Ctrl+C / V";
    private Hotkeys? _hotkeys;
    private CancellationTokenSource? _session;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { UpdateSummary(); CommandBox.Focus(); };
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new Hotkeys(new WindowInteropHelper(this).Handle);
            _hotkeys.OpenRequested += () =>
            {
                _session?.Cancel();
                Show();
                WindowState = WindowState.Normal;
                Activate();
                CommandBox.Focus();
            };
            _hotkeys.StopRequested += () => _session?.Cancel();
            if (!_hotkeys.OpenRegistered)
                HotkeyHint.Text = "Ctrl + Alt + V 已被占用，请从任务栏打开窗口。F8 仍可用于停止。";
        };
    }

    private TypingOptions GetOptions() => new(
        ModeBox.SelectedIndex == 0 ? TypingMode.ScanCode : TypingMode.Unicode,
        GetNumber(SpeedBox, 80), LineBreaksBox.IsChecked == true, AppendEnterBox.IsChecked == true);

    private static int GetNumber(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var number) ? number : fallback;

    private void Command_TextChanged(object sender, TextChangedEventArgs e) => UpdateSummary();
    private void Setting_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        if (CountLabel is null || SpeedBox is null || EnterHint is null) return;
        var normalized = CommandBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Count(c => c == '\n') + 1;
        var characters = normalized.EnumerateRunes().Count();
        CountLabel.Text = $"{characters:N0} 个字符 · {lines:N0} 行";
        var steps = characters + (AppendEnterBox.IsChecked == true ? 1 : 0);
        EstimateLabel.Text = steps == 0 ? "" : $"预计约 {Math.Ceiling(steps * GetNumber(SpeedBox, 80) / 1000d):N0} 秒";
        ModeHint.Text = ModeBox.SelectedIndex == 0
            ? "适用于英文命令。请使用英文输入法，关闭 Caps Lock，并将本地与远端键盘设为 US。"
            : "可输入中文等文本；部分远程 KVM / iBMC 不识别此方式。建议先在普通输入框试用。";
        EnterHint.Text = LineBreaksBox.IsChecked == true || AppendEnterBox.IsChecked == true
            ? "已启用 Enter：命令可能立即执行，请先核对目标和内容。"
            : "默认只输入，不执行。你可以在控制台检查后手动按回车。";
        if (_session is null) ErrorLabel.Visibility = Visibility.Collapsed;
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) { ShowError("剪贴板里没有文本。请先复制命令。"); return; }
            CommandBox.SelectedText = Clipboard.GetText();
            CommandBox.CaretIndex = CommandBox.SelectionStart + CommandBox.SelectionLength;
            CommandBox.Focus();
        }
        catch (ExternalException) { ShowError("剪贴板暂时被其他程序占用，请稍后重试。"); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { CommandBox.Clear(); CommandBox.Focus(); }
    private void Stop_Click(object sender, RoutedEventArgs e) => _session?.Cancel();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) return;
        var text = CommandBox.Text;
        var options = GetOptions();
        var validationError = KeyboardTyper.Validate(text, options);
        if (validationError is not null) { ShowError(validationError); return; }
        if (options.Mode == TypingMode.ScanCode && WindowTarget.IsCapsLockOn())
        {
            ShowError("请先关闭 Caps Lock，再开始输入。");
            return;
        }
        if (_hotkeys?.RegisterStop() != true)
        {
            ShowError("无法注册 F8 停止键（可能已被其他程序占用），本次未开始输入。");
            return;
        }

        using var session = new CancellationTokenSource();
        _session = session;
        SetRunning(true);
        ErrorLabel.Visibility = Visibility.Collapsed;
        SendProgress.Value = 0;
        var completed = 0;
        var total = 0;
        try
        {
            var seconds = GetNumber(CountdownBox, 5);
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                StatusLabel.Text = $"{remaining} 秒后开始，请点击目标控制台";
                ProgressLabel.Text = "把光标放到命令输入位置；F8 可取消。";
                Title = $"{remaining} 秒后输入 · {AppTitle}";
                await Task.Delay(1000, session.Token);
            }

            var target = WindowTarget.GetForegroundWindow();
            if (!WindowTarget.IsValid(target) || WindowTarget.IsOwnProcessWindow(target))
                throw new InvalidOperationException("没有选中其他窗口。请重新开始倒计时，并点击目标控制台的输入区域。");
            var targetTitle = WindowTarget.GetTitle(target);
            StatusLabel.Text = "正在模拟键盘输入…";
            ProgressLabel.Text = $"目标：{targetTitle}";
            var progress = new Progress<TypingProgress>(value =>
            {
                if (_session != session || session.IsCancellationRequested) return;
                completed = value.Completed;
                total = value.Total;
                SendProgress.Value = total == 0 ? 0 : completed * 100d / total;
                Title = $"{completed}/{total} · {AppTitle}";
                StatusLabel.Text = $"正在输入 {completed:N0} / {total:N0}";
            });
            await new KeyboardTyper().TypeAsync(text, target, options, progress, session.Token);
            StatusLabel.Text = "输入完成";
            ProgressLabel.Text = options.AppendEnter || (options.AllowLineBreaks && text.IndexOfAny(['\r', '\n']) >= 0)
                ? "已按设置发送 Enter，请在目标控制台核对内容与执行结果。"
                : "请在目标控制台核对内容。需要执行时，请手动按回车。";
            SendProgress.Value = 100;
            Title = $"已完成 · {AppTitle}";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "已停止";
            ProgressLabel.Text = completed > 0
                ? $"约已发送 {completed:N0} 个字符；再次开始会从头输入。"
                : "目标中可能已有部分内容，请检查后再重试；再次开始会从头输入。";
            Title = $"已停止 · {AppTitle}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
        {
            StatusLabel.Text = "输入已停止";
            ProgressLabel.Text = "请检查目标中的现有内容；再次开始会从头输入。";
            Title = $"已停止 · {AppTitle}";
            ShowError(ex.Message);
        }
        finally
        {
            _hotkeys?.UnregisterStop();
            _session = null;
            SetRunning(false);
            if (_closing) Close();
        }
    }

    private void SetRunning(bool running)
    {
        CommandBox.IsReadOnly = running;
        PasteButton.IsEnabled = ClearButton.IsEnabled = StartButton.IsEnabled = !running;
        ModeBox.IsEnabled = SpeedBox.IsEnabled = CountdownBox.IsEnabled = !running;
        LineBreaksBox.IsEnabled = AppendEnterBox.IsEnabled = !running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message) { ErrorLabel.Text = message; ErrorLabel.Visibility = Visibility.Visible; }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _session is not null) { _session.Cancel(); e.Handled = true; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_session is not null)
        {
            e.Cancel = true;
            _closing = true;
            _session.Cancel();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) { _hotkeys?.Dispose(); base.OnClosed(e); }
}
