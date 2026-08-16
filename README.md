# FluidDock

Windows 上的 macOS 风格 Dock，用 C# + `Windows.UI.Composition` 写的。

做它的起因是现有的同类软件（RocketDock / Nexus / MyDockFinder）动画质感都不行，而原因几乎是同一个：**动画跑在应用的 UI 线程上**，线程一被布局、图标加载、进程枚举阻塞就掉帧。macOS Dock 的流畅来自 Core Animation —— 动画由独立进程按刷新率求值，和应用逻辑彻底解耦。

Windows 上的等价物是 `Windows.UI.Composition`：`ExpressionAnimation` 由 DWM 合成线程求值。这条承诺是可测的，不是说说而已 —— `tools\StallTest.ps1` 会在 UI 线程上 `Thread.Sleep`，然后证明图标在 UI 线程确凿卡死期间仍在动。

目标环境：1920×1080 @ 239Hz、Windows 10 22H2 (19045)、100% 缩放。

---

## 构建与运行

这台机器上有两个坑，踩过了写在这里：

```powershell
# 必须用用户级 SDK。C:\Program Files\dotnet 只有运行时，没有 SDK。
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src\FluidDock\FluidDock.csproj -c Release -v q --nologo
```

- **不要用 `dotnet run`** —— 会失败。用户级 dotnet 根目录有运行时 8.0 和 10.0，唯独没有 9.0.x，而项目的目标框架是 net9.0。直接跑编译出来的 .exe，它的 apphost 会通过注册表找到 `C:\Program Files\dotnet` 里的 9.0.17。
- 构建前先 `Get-Process FluidDock | Stop-Process -Force`，否则复制步骤会报 MSB3027。
- 日志只在进程退出时落盘，所以磁盘上的日志**永远是上一次运行的**。

打包：

```powershell
tools\Publish.ps1        # -> dist\FluidDock.exe，自包含单文件，约 95 MB
```

自包含是有意的：dev 构建靠注册表找到系统里的 .NET 9，那是运气不是设计。压缩和 ReadyToRun 两个开关默认都关着，理由是实测出来的，见 `tools\Publish.ps1` 头部注释和 `tools\PackagingCost.ps1` —— 简单说，压缩省 92 MB 磁盘要拿 114 MB 内存和双倍启动时间换，因为压缩过的 bundle 条目无法内存映射，只能解压到私有堆上。

托盘右键有三项：**菜单…**（设置面板）、**显示 Dock**、**退出**。双击托盘图标等同于「菜单…」。

退出只有托盘这一条路，是有意的 —— 原来还有个 `Ctrl+Alt+Q`，但它注册在 Dock 窗口上，而那个窗口的生死由 Explorer 说了算（见下），所以它在最需要它的时候恰好不能用。

同时只能跑一个实例。

---

## 架构上几个不能动的地方

**每个图标是两层嵌套 visual。** 外层拥有 `Offset.X`（归放大镜的 ExpressionAnimation 管），内层拥有 `Offset.Y` 和 `Scale`（归弹跳管）。`Offset` 是单个 Vector3 属性，一条表达式绑上去就整个占住了 —— 不做这个拆分，弹跳和放大镜会互相覆盖。

**衰减函数和常量只有一处定义**，同时供表达式字符串和 C# 命中测试使用。两边各写一份迟早会漂移。

**基于时间的动画必须显式释放。** `ExpressionAnimation` 是依赖追踪的，空闲时零成本；但 KeyFrame / Spring 动画不 `StopAnimation`（配合 `CompositionScopedBatch`）就会永远请求逐帧回调。`tools\IdleTest.ps1` 守着这条。

**桌面层的窗口区域。** Dock 沉在桌面层时是 Progman 的子窗口，而整条桌面窗口链（Progman / SHELLDLL_DefView / SysListView32）都设了 `WS_CLIPSIBLINGS | WS_CLIPCHILDREN` —— 这是量出来的，不是猜的（`tools\DesktopStyles.ps1`）。意味着这棵树里任何位置的子窗口都会占住桌面拒绝绘制的像素，往更深处 reparent 也躲不掉，`SetWindowRgn` 是唯一的杠杆。所以窗口区域在空闲时收缩到只包住静止图标，**立即扩张、延迟收缩**：UI 线程的窗口属性追不上合成线程的动画状态，放大收尾要 280ms，弹跳可以持续数秒。

---

## 设置面板

托盘 →「菜单…」。macOS 深色模式的配色，圆角卡片分组、开关、滑块、分段控件，悬停高亮用弹簧在行之间滑动，开合是缩放 + 淡入淡出。

### Dock 里放什么，在这里改

面板第一组就是「应用」，一行一项：缩略图、名字，以及**只在悬停时浮出**的操作 ——「图标」换一张自定义图标，「默认」还原（只有设过自定义图标的行才有它），末尾一个 `×` 移除。下面一张卡片是两个添加按钮。

| 操作 | 说明 |
|---|---|
| 添加应用或文件… | Windows 自己的文件对话框，可多选。过滤器给了 `exe / lnk / url / bat / cmd / msc / appref-ms`，但另一档是「所有文件」—— 能被 shell 打开的都行 |
| 添加文件夹… | 同一个对话框，`FOS_PICKFOLDERS` |
| 图标 | png / ico 都收；**也可以直接选另一个 exe 或 dll**，意思是「借它的图标」。选中的图片如果在程序目录里，存的是相对路径 |
| 拖动 | 按住任意一行上下拖，其他行让开，松手落位。按在「图标 / 默认 / ×」上不触发拖动，那是点击 |

**快捷方式是被解引用的**（没有传 `FOS_NODEREFERENCELINKS`）：选一个 `.lnk`，存进配置的是它指向的目标。因为 `.lnk` 自己的 shell 图标带着左下角那个箭头角标，直接用会在 Dock 里留一排箭头。

改完照例写回 `dock.json`，Dock 的文件监视器重载 —— 和滑块走的是同一条路。

三个不显眼但不能动的地方：

- **任何会改列表的操作都必须延后到指针事件之外。** 移除会触发面板重建，而重建会 `Dispose` 掉此刻正在派发这个指针事件的那个面板。所以延后这件事写在 `DockItems` 里而不是各个行里 —— 以后新写的行没有机会忘。
- **`SettingsStore.Reload()` 只在文件字节真的变了时才替换 `Config`。** 行持有的是 `DockItemConfig` 的**引用**；每次开面板都无条件重新 Load，等于让行去改一个没人会保存的列表。所以 `Reload` 返回「换没换」，换了才重建面板。
- **落位判断用的是未夹取的位置。** 拖动中的行为了画面好看被夹在卡片范围内，但用夹取后的值算目标槽位，第一格永远够不到。另外那个比较必须是 `<=`：行静止时它的中心恰好落在补位那一行的中线上，用 `<` 会在用户还没动之前就先报「下一格」。

### 加一个设置项 = 一行

面板的内容全部写在 `Menu\MenuDefinition.cs` 里，加一个设置就是往列表里加一行：

```csharp
new SliderRow("图标大小", 24f, 96f, 2f,
    () => Metrics().IconSize,
    value => Metrics().IconSize = value,
    value => $"{value:0} px"),
```

布局、命中测试、动画、保存、生效，全都不用碰。现成六种行：

| 行 | 用途 |
|---|---|
| `ToggleRow` | 开 / 关（macOS 开关，旋钮走弹簧、轨道颜色交叉淡入） |
| `SliderRow` | 区间里的一个数 |
| `SegmentRow` | 几个互斥选项（选中块在段之间滑动） |
| `ButtonRow` | 一个动作，可标红、可顺带关面板 |
| `LabelRow` | 只读 |
| `DockItemRow` | Dock 里的一项：缩略图、名字、悬停才浮出的操作 |

第七种就是在 `Menu\Rows\` 下新建一个文件实现 `MenuRow` —— 面板本身不需要知道它存在。`MenuRow` 只要求一个 `Build`，指针事件按需重写；一切绘制都走 `MenuCanvas`，所以新写的行自动继承了「描边内缩半像素」「动画必须释放」这些已经踩过的坑。

### 面板怎么把设置传给 Dock

**不传。** 面板改的是自己那份 `DockConfig`，然后写回 `dock.json`；Dock 原有的文件监视器发现改动，自己重载重建。这条路和手改配置文件是同一条 —— 所以滑块能做到的事，手写配置一定也能，两边不会长出对方没有的分支。

唯一的例外是**层级**：它决定窗口的扩展样式和是不是桌面的子窗口，这两样在 `CreateWindowEx` 时就定死了，重建视觉树改不动。所以 `ReloadConfig` 发现层级变了会走 `Recreate()` 换一个窗口。一个需要重启才生效的开关等于一个说谎的开关。

滑块**松手才写盘**。拖动过程中连续保存是正确且不可用的：Dock 每次配置变化都会重建整棵视觉树，两秒的拖动会让它重新光栅化上百次图标。

---

## 测试工具

`tools\` 下都是 PowerShell 脚本，每个都是量出结论而不是看着像。

| 脚本 | 证明什么 |
|---|---|
| `StallTest.ps1` | UI 线程卡死时动画仍在跑（整个架构的决定性测试） |
| `IdleTest.ps1` | 空闲时没有逐帧回调泄漏 |
| `InputTest.ps1` | 500 次移动里 Dock 实际处理了多少 —— 掉采样就是"卡顿"从内部看的样子。`-Exe` 选择测哪个构建 |
| `RegionTailTest.ps1` | 窗口区域没有裁掉放大/弹跳的收尾 |
| `MarqueeShot.ps1` | 桌面框选划过 Dock 时不出现空洞 |
| `BounceTest.ps1` | 点击→启动→弹跳→停止的完整链路 |
| `HideShowTest.ps1` | 托盘隐藏再显示后 Dock 仍然完好 |
| `TrayMenuTest.ps1` | 托盘菜单命令有效，退出会清理图标 |
| `MenuTest.ps1` | 设置面板能打开、三种控件都写得进配置、Dock 跟着变、三种关法都有效。点击坐标是按 `MenuTheme` 的常量算出来的，等于把布局又独立写了一遍 |
| `ItemsTest.ps1` | 拖动排序真的改了 `dock.json`、拖回去能还原、移除删对了行、面板绕右下角重建、重建后的行改的仍是活着的那个列表 |
| `MenuIdleTest.ps1` | 面板开着、悬停过、关掉之后都不留逐帧回调 |
| `ExplorerRestartTest.ps1` | Explorer 重启后 Dock 自己重建、回到桌面层、还会放大，且退出仍然有效 |
| `PackagingCost.ps1` | 四种打包方式的磁盘/内存/启动对比 |

### 写这些脚本时反复踩的坑

- **`.ps1` 文件必须带 UTF-8 BOM。** Windows PowerShell 5.1 按系统 ANSI（这里是 GBK）解析脚本，中文字面量会被静默改写 —— `"退出"` 变成 `"閫€鍑?"`。阴险在于文件本身是合法 UTF-8，运行时输出也正常，只表现为"本该匹配的比较不匹配"。
- **UI Automation 看不见 shell 的这些窗口。** 它不下钻到托盘的 `ToolbarWindow32`，对弹出菜单也返回空树。托盘图标要靠直接读工具栏（`TB_GETBUTTON` + 跨进程内存读 `TRAYDATA`），菜单要靠 `MN_GETHMENU` + `GetMenuStringW`。
- **PowerShell 把 `$null` 强转成 `""`** 传给字符串 P/Invoke 参数，所以 `FindWindowW("Progman", $null)` 什么都匹配不到。这个 bug 在两个不同脚本里各犯了一次。
- **函数会把输出流上的一切当返回值。** 函数里的 `Write-Output` 会被调用方连同真正的返回值一起收走 —— 早期版本因此把一个字符串当成"找到的托盘图标"报了出来。函数内一律用 `[Console]::WriteLine`。
- **截屏的 alpha 恒为 255**，`CopyFromScreen` 出来的位图不带透明度，拿 alpha 判断图标位置等于什么都没测。饱和度阈值也不通用（它假设桌面是灰的）。可靠的做法是**与静止帧做差分**。
- **合成按键进不去系统的文件对话框。** 三条路都试过：`SendKeys.SendWait`（静默地什么都不做 —— .NET 会回退到 journal playback，Vista 起就被禁了）、往 `ComboBoxEx32 > ComboBox > Edit` 发 `SetWindowTextW`（文字确实进去了，但「打开」仍然按空文件名走）、`keybd_event` + `VkKeyScanW` 逐字符敲（先点中编辑框自己的矩形也没用，读回来还是空的）。同样的 `keybd_event` 打 FluidDock 自己的窗口是有效的（`MenuTest` 的 Esc 就靠它）。所以 `ItemsTest` 能验证对话框**弹得对**（标题、按钮文字、过滤器、面板不被它挤掉），但「选中文件之后加没加进去」那一段是人工验收的。

---

## Explorer 重启

Dock 是 Progman 的子窗口，所以 Explorer 一重启，Dock 窗口就被销毁 —— 进程还活着，但屏幕上什么都没有。曾经这是个死局：退出是往 Dock 窗口发 `WM_CLOSE`，热键也注册在同一个窗口上，两条路一起断，只能开任务管理器。

现在两条都不再依赖那个窗口：

| | 做法 |
|---|---|
| 退出 | 直接 `PostQuitMessage`，走托盘窗口 —— 它是独立的顶层窗口，Explorer 拿不走 |
| 重建 | 托盘窗口收到 `TaskbarCreated` 后调 `DockWindow.Recreate()` |
| 保留的东西 | Compositor、SurfaceFactory（及其 D3D 设备）、配置监视器、前台钩子。只有 HWND 和绑在它上面的 `DesktopWindowTarget` 需要换 |
| 何时认爹 | 等 `SHELLDLL_DefView` 出现再 `SetParent`，每 500ms 试一次，最多 10 秒 |

最后一条不是保险起见。`TaskbarCreated` 是在 Explorer 还没装配完时广播的，那时 Progman 可能已经在了而图标视图还没有；此刻 reparent 进去，图标视图随后创建并落在 z-order 顶上，结果是一个**画得出来但点不动**的 Dock。等视图出现就不会落到那个位置。等待期间 Dock 是普通窗口 —— 在屏幕上、能用，只是没沉进桌面。

`WM_DESTROY` 因此**不再** `PostQuitMessage`：这个窗口的死不等于程序的死。

## 已知限制

- 静止时图标之间那 14px 的间隙在窗口区域之外，光标正好从间隙进入时要碰到图标才会触发放大。
- 弹跳期间区域完全打开，此刻恰好在拖框选会看到空洞。
- 启动弹跳大部分时候被拉起的应用窗口盖住了。
- **第一次打开设置面板要涨约 40 MB 私有内存**（实测：46.3 → 86.6 MB，句柄 1347 → 1648）。反复开关不再涨，面板只构建一次；关掉也不还回去，换的是下次秒开。大头是 GDI+ 走 DirectWrite 渲染中文时载入的字体栈（`DWrite.dll` + `TextShaping.dll` + 雅黑的字形数据），不是泄漏。面板是**懒构建**的，从不打开就一分不花。
- 设置面板没有背景模糊，是烤好的半透明底 + 噪点。`ACCENT_ENABLE_BLURBEHIND` 模糊的是整个窗口矩形，而窗口比面板大一圈（要留投影的地方），所以模糊会露成一个直角方块；用 `SetWindowRgn` 裁成圆角又会把投影一起裁掉。
- 面板是按 100% 缩放画的，文字光栅化在 96 DPI。换到缩放显示器上会糊。
- 托盘图标默认进溢出区（任务栏的「^」里），需要手动拖到任务栏上才常驻。这是 Win10 对新注册图标的默认行为。
