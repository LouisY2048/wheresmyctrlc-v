namespace WheresMyCtrlCV.Input;

public enum TypingMode
{
    ScanCode,
    Unicode
}

public sealed record TypingOptions(
    TypingMode Mode,
    int CharacterDelayMs,
    bool AllowLineBreaks,
    bool AppendEnter);

public sealed record TypingProgress(int Completed, int Total);
