# Does the settings panel work?
#
# Three claims, in order, because a later one is meaningless if an earlier one failed:
#
#   1. the tray's menu item opens a panel, on screen and inside the work area
#   2. its controls do what they say - a segmented control, a slider, and a switch
#   3. the dock notices, and the panel dismisses the three ways it is supposed to
#
# Clicks are aimed at computed coordinates rather than found by searching the screen: every
# position in the panel follows from the constants in MenuTheme and the row order in
# MenuDefinition, so the arithmetic below is a second, independent statement of the layout. If
# the panel is laid out differently than intended, these clicks land on the wrong control and
# the assertions fail - which is the point.
#
# Since the dock's item list moved to the top of the panel, most of the controls start below the
# fold, so the script scrolls to reach them. It scrolls to an exact offset rather than "far
# enough": a wheel notch is 52px and the scroll is clamped, so sending one message of a known
# size puts the content at a position this file can do arithmetic with. Getting the content
# height wrong makes every click after the first scroll land on the wrong row.
#
# The config file is backed up byte-for-byte and restored at the end, including on failure.
#
# Kept ASCII-only on purpose: Windows PowerShell 5.1 parses .ps1 as the system ANSI code page
# unless the file carries a UTF-8 BOM, so a Chinese literal here would be silently corrupted.

param(
    [string] $Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) "src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MT {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string w);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint c);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    // Wrapped so C# passes a real null. PowerShell 5.1 coerces $null to "" for string
    // parameters, and FindWindow with an empty title matches nothing.
    public static IntPtr Menu() { return FindWindowW("FluidDockMenu", null); }
    public static IntPtr Dock() { return FindWindowW("FluidDockWindow", null); }

    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static bool Visible(IntPtr h) { return h != IntPtr.Zero && IsWindowVisible(h); }
    public static RECT Work() { RECT r = new RECT(); SystemParametersInfoW(0x0030, 0, ref r, 0); return r; }

    public static void Left()   { mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0004,0,0,0,IntPtr.Zero); }
    public static void Wheel(int notches) { mouse_event(0x0800,0,0,unchecked((uint)(notches * 120)),IntPtr.Zero); }
    public static void Escape() { keybd_event(0x1B,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B,0,2,IntPtr.Zero); }
}
'@

# ---- Layout, restated ---------------------------------------------------------------------
# From MenuTheme. If any of these change, this script has to change with them - deliberately,
# so that a layout change cannot pass unnoticed.
$ShadowMargin  = 34
$HeaderHeight  = 54
$FooterHeight  = 12
$PanelPadX     = 14
$RowPadX       = 14
$PanelWidth    = 344
$RowHeight     = 44
$TallRowHeight = 64
$SectionHeader = 28
$SectionGap    = 14
$SliderKnob    = 16
$MaxViewport   = 560
$NotchPixels   = 52
$CardWidth     = $PanelWidth - $PanelPadX * 2

function Move-To($p) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$p.X), ([int]$p.Y)
    Start-Sleep -Milliseconds 200
}

function Click-At($p) { Move-To $p; [MT]::Left(); Start-Sleep -Milliseconds 250 }

# PowerShell 5.1 reads a BOM-less UTF-8 file as the ANSI code page, which turns this JSON into
# mojibake and makes ConvertFrom-Json fail with "Unterminated string passed in".
function Read-Config($path) {
    [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}

$pass = @()
$fail = @()
function Check($name, $condition, $detail) {
    if ($condition) { $script:pass += "PASS  $name  $detail" }
    else            { $script:fail += "FAIL  $name  $detail" }
}

$configPath = Join-Path (Split-Path $Exe) "config\dock.json"
$backup = $null
$saved = [System.Windows.Forms.Cursor]::Position

$script:PanelL = 0
$script:PanelT = 0
$script:Scroll = 0

try {
    if (-not (Get-Process FluidDock -ErrorAction SilentlyContinue)) {
        Start-Process $Exe
        Start-Sleep -Seconds 3
    }
    if (-not (Test-Path $configPath)) { throw "no config at $configPath" }
    $backup = [System.IO.File]::ReadAllBytes($configPath)

    $before = Read-Config $configPath
    $items = @($before.Items).Count

    # Row tops in content coordinates, walked the same way MenuPanel walks them. The item list
    # comes first and is as long as the config says, so everything below it moves when the user
    # adds an app - which is exactly the arithmetic this is here to keep honest.
    $y = 0
    $Top = @{}

    $y += $SectionHeader                      # 应用
    $Top["FirstItem"] = $y
    $y += $items * $RowHeight + $SectionGap

    $Top["AddPrograms"] = $y                  # the add buttons carry no header
    $Top["AddFolder"] = $y + $RowHeight
    $y += 2 * $RowHeight + $SectionGap

    $y += $SectionHeader                      # 常规
    $Top["ShowDock"]  = $y; $y += $RowHeight
    $Top["AutoStart"] = $y; $y += $RowHeight + $SectionGap

    $y += $SectionHeader                      # 外观
    $Top["Theme"]    = $y; $y += $RowHeight
    $Top["IconSize"] = $y; $y += $TallRowHeight
    $Top["IconGap"]  = $y; $y += $TallRowHeight + $SectionGap

    $y += $SectionHeader                      # 放大
    $Top["MaxScale"]       = $y; $y += $TallRowHeight
    $Top["InfluenceCells"] = $y; $y += $TallRowHeight
    $Top["BounceHeight"]   = $y; $y += $TallRowHeight + $SectionGap

    $y += $SectionHeader                      # 位置
    $Top["Layer"]        = $y; $y += $RowHeight
    $Top["ScreenMargin"] = $y; $y += $TallRowHeight + $SectionGap

    # One row here until a check finds a newer version, which adds a notes row of content-sized
    # height under it. Nothing in this script checks for updates, so it is always one row.
    $y += $SectionHeader                      # 更新
    $Top["Update"] = $y; $y += $RowHeight + $SectionGap

    $y += $SectionHeader                      # 关于
    $Top["Adapter"]    = $y; $y += $RowHeight
    $Top["OpenConfig"] = $y; $y += $RowHeight
    $Top["Quit"]       = $y; $y += $RowHeight

    $ContentHeight = $y + $FooterHeight
    $Viewport = [Math]::Min($ContentHeight, $MaxViewport)
    $Range = $ContentHeight - $Viewport

    # ---- 1. the panel opens -----------------------------------------------------------------
    & "$PSScriptRoot\TrayToggle.ps1" -Menu
    Start-Sleep -Milliseconds 700

    $menu = [MT]::Menu()
    Check "menu window exists" ($menu -ne [IntPtr]::Zero) ("hwnd=0x{0:X}" -f [int64]$menu)
    if ($menu -eq [IntPtr]::Zero) { throw "no menu window; nothing further can be tested" }

    $rect = [MT]::Rect($menu)
    $script:PanelL = $rect.L + $ShadowMargin
    $script:PanelT = $rect.T + $ShadowMargin
    $work = [MT]::Work()

    Check "menu visible" ([MT]::Visible($menu)) "rect=$($rect.L),$($rect.T) $($rect.R-$rect.L)x$($rect.B-$rect.T)"
    Check "inside work area" (($rect.L + $ShadowMargin) -ge $work.L -and ($rect.R - $ShadowMargin) -le $work.R -and ($rect.T + $ShadowMargin) -ge $work.T -and ($rect.B - $ShadowMargin) -le $work.B) "work=$($work.L),$($work.T)-$($work.R),$($work.B)"
    Check "panel width" (($rect.R - $rect.L) -eq ($PanelWidth + $ShadowMargin * 2)) "$($rect.R - $rect.L) px"

    # The panel's height is the one number that comes from the content rather than from a
    # constant, so it is worth checking that both sides agree on what the content adds up to.
    $expectedH = $HeaderHeight + $Viewport + $(if ($Range -gt 0) { 10 } else { 0 }) + $ShadowMargin * 2
    Check "panel height matches the content" (($rect.B - $rect.T) -eq $expectedH) `
        "expected $expectedH, got $($rect.B - $rect.T) (content $ContentHeight, $items items)"

    # Scrolls the content to an exact offset and returns it. One wheel message of a known size,
    # after one large one to get back to the top - the scroll is clamped at both ends, so both
    # land on a position that can be predicted rather than measured.
    function Scroll-For($top) {
        $want = [Math]::Max(0, $top - 120)
        $notches = [int][Math]::Ceiling($want / $NotchPixels)
        $target = [Math]::Min($notches * $NotchPixels, $Range)

        Move-To @{ X = $script:PanelL + [int]($PanelWidth / 2); Y = $script:PanelT + $HeaderHeight + 120 }
        [MT]::Wheel(40)
        Start-Sleep -Milliseconds 450
        if ($notches -gt 0) { [MT]::Wheel(-$notches); Start-Sleep -Milliseconds 550 }

        $script:Scroll = $target
        $target
    }

    function Row-Point($name, $height) {
        @{
            X = $script:PanelL + $PanelPadX + [int]($CardWidth / 2)
            Y = $script:PanelT + $HeaderHeight + $Top[$name] - $script:Scroll + [int]($height / 2)
        }
    }

    function Slider-Point($name, $t) {
        $travelLeft  = $RowPadX + $SliderKnob / 2
        $travelWidth = $CardWidth - $RowPadX * 2 - $SliderKnob
        @{
            X = $script:PanelL + $PanelPadX + [int]($travelLeft + $t * $travelWidth)
            Y = $script:PanelT + $HeaderHeight + $Top[$name] - $script:Scroll + 44
        }
    }

    function Segment-Point($name, $index, $count, $segWidth) {
        $wellLeft = $CardWidth - $RowPadX - $segWidth * $count
        @{
            X = $script:PanelL + $PanelPadX + [int]($wellLeft + ($index + 0.5) * $segWidth)
            Y = $script:PanelT + $HeaderHeight + $Top[$name] - $script:Scroll + [int]($RowHeight / 2)
        }
    }

    # ---- 2. controls do what they say -------------------------------------------------------
    # The layer first, because it is the one that makes the dock a top-level window - and until
    # it is, FindWindow cannot see it at all: on the desktop layer the dock is a child of WorkerW.
    Scroll-For $Top["Layer"] | Out-Null
    Click-At (Segment-Point "Layer" 2 3 44)
    Start-Sleep -Milliseconds 1400
    $after = Read-Config $configPath
    Check "segment sets Layer" ($after.Layer -eq "Top") "expected Top, got $($after.Layer)"

    # t = 0.75 over the 24..96 range, snapped to the 2px step, is 78.
    Scroll-For $Top["IconSize"] | Out-Null
    Click-At (Slider-Point "IconSize" 0.75)
    Start-Sleep -Milliseconds 1000
    $after = Read-Config $configPath
    Check "slider sets IconSize" ($after.Metrics.IconSize -eq 78) "expected 78, got $($after.Metrics.IconSize)"

    $dock = [MT]::Dock()
    Check "dock still alive" ($dock -ne [IntPtr]::Zero) ("hwnd=0x{0:X}" -f [int64]$dock)
    if ($dock -ne [IntPtr]::Zero) {
        $d = [MT]::Rect($dock)
        # 7 icons at 78px instead of 48px cannot fit in the old window.
        Check "dock rebuilt wider" (($d.R - $d.L) -gt 700) "$($d.R - $d.L) px wide"
    }

    # The show switch is live state rather than a saved setting, so it is checked against the
    # dock itself. Flipped twice, because leaving the dock hidden would outlast this script -
    # restoring the config file cannot put back something the config file does not hold.
    Scroll-For $Top["ShowDock"] | Out-Null
    Click-At (Row-Point "ShowDock" $RowHeight)
    Start-Sleep -Milliseconds 700
    Check "switch hides the dock" (-not [MT]::Visible([MT]::Dock())) "dock visible=$([MT]::Visible([MT]::Dock()))"

    Click-At (Row-Point "ShowDock" $RowHeight)
    Start-Sleep -Milliseconds 700
    Check "switch shows it again" ([MT]::Visible([MT]::Dock())) "dock visible=$([MT]::Visible([MT]::Dock()))"

    # ---- 3. scrolling, and the three ways out ------------------------------------------------
    Scroll-For 0 | Out-Null
    Move-To (Row-Point "FirstItem" $RowHeight)
    $shotBefore = New-Object System.Drawing.Bitmap ($PanelWidth), 200
    $g = [System.Drawing.Graphics]::FromImage($shotBefore)
    $g.CopyFromScreen($script:PanelL, ($script:PanelT + $HeaderHeight), 0, 0, $shotBefore.Size); $g.Dispose()
    [MT]::Wheel(-3)
    Start-Sleep -Milliseconds 700
    $shotAfter = New-Object System.Drawing.Bitmap ($PanelWidth), 200
    $g = [System.Drawing.Graphics]::FromImage($shotAfter)
    $g.CopyFromScreen($script:PanelL, ($script:PanelT + $HeaderHeight), 0, 0, $shotAfter.Size); $g.Dispose()

    $diff = 0
    for ($py = 0; $py -lt 200; $py += 4) {
        for ($px = 0; $px -lt $PanelWidth; $px += 4) {
            if ($shotBefore.GetPixel($px, $py).ToArgb() -ne $shotAfter.GetPixel($px, $py).ToArgb()) { $diff++ }
        }
    }
    $shotBefore.Dispose(); $shotAfter.Dispose()
    Check "wheel scrolls content" ($diff -gt 200) "$diff of $([int](200/4) * [int]($PanelWidth/4)) sampled pixels changed"

    [MT]::Escape()
    Start-Sleep -Milliseconds 600
    Check "escape hides panel" (-not [MT]::Visible([MT]::Menu())) "visible=$([MT]::Visible([MT]::Menu()))"

    & "$PSScriptRoot\TrayToggle.ps1" -Menu
    Start-Sleep -Milliseconds 700
    Check "reopens" ([MT]::Visible([MT]::Menu())) "visible=$([MT]::Visible([MT]::Menu()))"

    # Clicking away deactivates the window, which is what dismisses it. Aimed at bare wallpaper,
    # well clear of both the panel and the dock.
    Click-At @{ X = 200; Y = 300 }
    Start-Sleep -Milliseconds 600
    Check "click away hides panel" (-not [MT]::Visible([MT]::Menu())) "visible=$([MT]::Visible([MT]::Menu()))"
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
    if ($backup) {
        [System.IO.File]::WriteAllBytes($configPath, $backup)
        Start-Sleep -Milliseconds 900
    }
}

$pass | ForEach-Object { $_ }
$fail | ForEach-Object { $_ }
""
if ($fail.Count -eq 0) { "MenuTest PASS  ($($pass.Count) checks)" }
else { "MenuTest FAIL  ($($fail.Count) of $($pass.Count + $fail.Count) checks)" }
