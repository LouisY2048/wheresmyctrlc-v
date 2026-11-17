namespace WheresMyCtrlCV.Input;

internal readonly record struct KeyboardEvent(ushort ScanCode, uint Flags)
{
    internal const uint KeyUp = 0x0002;
    internal const uint Unicode = 0x0004;
    internal const uint Scan = 0x0008;

    internal bool IsKeyUp => (Flags & KeyUp) != 0;
    internal KeyboardEvent AsKeyUp() => this with { Flags = Flags | KeyUp };
}

internal static class InputPlanner
{
    private static readonly Dictionary<char, (ushort ScanCode, bool Shift)> UsKeys = BuildUsKeys();

    internal static bool TryBuild(string? text, TypingOptions? options,
        out List<KeyboardEvent[]> plan, out string? error)
    {
        plan = [];
        error = null;
        if (options is null || !Enum.IsDefined(options.Mode))
            return Fail("请选择有效的输入模式。", out error);
        if (options.CharacterDelayMs is < 1 or > 2000)
            return Fail("每个字符的间隔需要在 1 到 2000 毫秒之间。", out error);
        if (string.IsNullOrEmpty(text))
            return Fail("请先填入需要输入的命令。", out error);

        // Normalize only line endings. Do not trim or otherwise change the command.
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        for (int index = 0; index < normalized.Length; index++)
        {
            char character = normalized[index];
            if (character == '\n')
            {
                if (!options.AllowLineBreaks)
                    return Fail("内容包含换行（也可能是末尾的换行）。请删除换行，或勾选允许换行；换行会向控制台发送 Enter。", out error);
                plan.Add(ScanKey(0x1C, false));
                continue;
            }

            if (char.IsControl(character) || character is '\u2028' or '\u2029')
            {
                string name = character == '\t' ? "Tab（可能触发补全）" : $"控制字符 U+{(int)character:X4}";
                return Fail($"内容包含 {name}，请先移除或替换为空格。", out error);
            }

            if (options.Mode == TypingMode.ScanCode)
            {
                if (!UsKeys.TryGetValue(character, out var key))
                    return Fail($"远程控制台模式仅支持英文、数字和英文符号。第 {index + 1} 个字符 U+{(int)character:X4} 不受支持；中文请尝试 Unicode 模式。", out error);
                plan.Add(ScanKey(key.ScanCode, key.Shift));
                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[index + 1]))
                    return Fail("内容包含不完整的 Unicode 字符，请重新复制内容。", out error);
                // Keep the entire surrogate pair in a single SendInput batch.
                plan.Add([
                    new(character, KeyboardEvent.Unicode),
                    new(character, KeyboardEvent.Unicode | KeyboardEvent.KeyUp),
                    new(normalized[index + 1], KeyboardEvent.Unicode),
                    new(normalized[index + 1], KeyboardEvent.Unicode | KeyboardEvent.KeyUp)
                ]);
                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return Fail("内容包含不完整的 Unicode 字符，请重新复制内容。", out error);
            }
            else
            {
                plan.Add([
                    new(character, KeyboardEvent.Unicode),
                    new(character, KeyboardEvent.Unicode | KeyboardEvent.KeyUp)
                ]);
            }
        }

        if (options.AppendEnter)
            plan.Add(ScanKey(0x1C, false));
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static KeyboardEvent[] ScanKey(ushort scanCode, bool shift) => shift
        ? [new(0x2A, KeyboardEvent.Scan), new(scanCode, KeyboardEvent.Scan),
            new(scanCode, KeyboardEvent.Scan | KeyboardEvent.KeyUp), new(0x2A, KeyboardEvent.Scan | KeyboardEvent.KeyUp)]
        : [new(scanCode, KeyboardEvent.Scan), new(scanCode, KeyboardEvent.Scan | KeyboardEvent.KeyUp)];

    private static Dictionary<char, (ushort ScanCode, bool Shift)> BuildUsKeys()
    {
        var keys = new Dictionary<char, (ushort, bool)>();
        AddRow("1234567890-=", "!@#$%^&*()_+", 0x02);
        AddRow("qwertyuiop[]", "QWERTYUIOP{}", 0x10);
        AddRow("asdfghjkl;'", "ASDFGHJKL:\"", 0x1E);
        AddRow("zxcvbnm,./", "ZXCVBNM<>?", 0x2C);
        AddRow("`", "~", 0x29);
        AddRow("\\", "|", 0x2B);
        keys.Add(' ', (0x39, false));
        return keys;

        void AddRow(string lower, string upper, ushort start)
        {
            for (int i = 0; i < lower.Length; i++)
            {
                keys.Add(lower[i], ((ushort)(start + i), false));
                keys.Add(upper[i], ((ushort)(start + i), true));
            }
        }
    }
}
