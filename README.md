# Where’s my Ctrl+C / V

给不能粘贴的控制台，补一个「帮你打字」的 Windows 小工具。

把命令贴进弹窗，开始倒计时，再点击远程控制台的输入位置。工具通过 Windows `SendInput` 模拟键盘逐字输入，不依赖目标的剪贴板功能。面向远程服务器、网页 KVM、华为 iBMC 等场景；实际兼容性取决于控制台是否接受 Windows 合成按键，需要在对应设备上试用。

## 运行

发布后双击 `artifacts/win-x64/WheresMyCtrlCV.exe`。它包含 .NET 运行时，无需安装 Python、Node.js 或 .NET。目标平台为 Windows 10/11 x64；也可自行发布 ARM64 版本。首次启动会解包随附的本机运行库。

从源码运行需要 **.NET 10 SDK（Windows）**：

```powershell
dotnet run --project src/WheresMyCtrlCV/WheresMyCtrlCV.csproj -c Release
```

生成可独立运行的单文件 EXE（首次发布需要联网下载运行时包）：

```powershell
.\publish.ps1
# ARM64：.\publish.ps1 -Runtime win-arm64
```

## 使用

1. 打开工具，将命令粘贴到输入框。运行期间可用 **Ctrl + Alt + V** 唤起窗口。
2. 使用默认「实体按键」模式。本地选择英文 US 键盘／英文输入状态，远端也设为 US 键盘，关闭 Caps Lock。
3. 点击「开始倒计时」，在 5 秒内点击目标控制台内实际接受键盘输入的位置。需要时可调成 8 或 10 秒。
4. 松开 Ctrl、Alt、Shift 等按键，等待输入完成。输入期间不要移动焦点或手动输入。
5. 在控制台核对命令，自己按 Enter 执行。工具默认**不按回车**。

**F8** 在倒计时和输入期间全局停止；工具窗口内也可按 Esc 或点击停止。按 Ctrl + Alt + V 返回工具会先停止当前输入。完成或停止不会主动抢走目标窗口的焦点。重新开始会从头输入，不会接着上次的位置续传。

如果快捷键已被其他应用占用，会显示提示；无法注册 F8 时不会开始输入。

## 输入方式

| 选项 | 适用情况 | 注意 |
| --- | --- | --- |
| 实体按键（默认） | 远程控制台、网页 KVM、iBMC、英文命令 | 使用 US 扫描码，支持可打印 ASCII 及常见命令符号，包含引号、反斜杠、管道符、美元符号等 |
| Unicode | 普通 Windows 编辑框，中文等文字 | 使用 Unicode 按键事件；部分远程控制台不会转发这些事件 |

默认每个字符间隔 80 ms。如果控制台丢字，可改成 150 或 300 ms。发送进度表示交给 Windows 的进度，不能证明远端已完整接收；发送结束后仍需核对。

## 换行与保护

- 默认拒绝任何换行，包括复制时带上的末尾换行。删除换行后可发送单条命令。
- 勾选「允许命令中的换行发送 Enter」后，每个换行会按一次 Enter，**可能逐行执行命令**。CRLF 算一次换行，空行也算。
- 「全部输入后，再按一次 Enter」独立生效。若文本本身以换行结束且两个选项均开启，会再额外按一次 Enter。
- 保留前后空格，不自动修剪或改写命令。TAB、控制字符和实体按键模式下无法表示的字符会在输入前整段检查并拒绝，避免输入一半才失败。
- 在倒计时结束时选择当前前台窗口；每次发送前检查窗口是否仍在前台且有效。拒绝把工具自身作为目标。按着修饰键或实体按键模式下开启 Caps Lock 时停止。
- **窗口检查有边界**：无法保证检测到同一浏览器内切换标签页、点击地址栏、网页弹窗或远端其他人的操作。请保持控制台焦点，必要时按 F8。检查与操作系统投递间也存在极短的竞态，不能作为绝对隔离保证。
- 窗口关闭时会先取消输入。输入内容仅保留在应用内存，不写历史文件或日志；应用没有联网发送命令的功能。点击粘贴才读取剪贴板，不监视剪贴板。

## 常见问题

**控制台没有收到文字？** 先确认点击了控制台内部，再切到实体按键模式。某些 KVM 只接受特定事件，某些控制台会过滤合成输入；此时不能保证兼容。该工具不会绕过控制台的权限限制。

**大写、符号不对？** 本地和远端都使用英文 US 键盘，关闭 Caps Lock，并退出中文输入状态。实体按键模式按 US 键位产生输入，不会自动切换你的输入法或远端布局。

**目标以管理员身份运行？** Windows 的 UIPI 会限制低权限进程向高权限进程注入。若目标确实需要管理员权限，需手动以相同权限启动本工具。工具不会自行提权；一般浏览器控制台不需要管理员权限。

**能后台输入吗？** 当前版本向前台窗口输入。输入期间需保留目标在前台，切到其他窗口会停止。

## 开发与检查

原生 C# / WPF，.NET 10，无第三方运行依赖。

```powershell
dotnet build src/WheresMyCtrlCV/WheresMyCtrlCV.csproj -c Release
dotnet run --project tests/EngineChecks -c Release
dotnet run --project tests/WindowsSmoke -c Release -- --run
```

`EngineChecks` 是不发送真实按键的回归检查。`WindowsSmoke` 会打开专用测试文本框，并向它发送无害的示例文本；请在可交互的 Windows 桌面运行，测试期间不要操作键盘鼠标。它不使用真实终端，也不执行命令。真实 iBMC / KVM 仍需设备实测。

关键实现：`Input/InputPlanner.cs` 负责预检与 US 键位映射，`Input/KeyboardTyper.cs` 负责逐字输入、取消与窗口检查，`Hotkeys.cs` 管理全局快捷键。

实现依据：[Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)。
