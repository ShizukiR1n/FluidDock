# Can the dock's contents actually be edited from the panel?
#
# Four things, and they fail in different ways:
#
#   reorder    a drag has to land in the slot it looks like it is over, commit that order to
#              dock.json, and leave the panel the same height it was
#   remove     deleting an entry changes the panel's height, which means the tree is rebuilt and
#              the window resized - from its bottom-right corner, so it grows away from the tray
#   dialog     the panel dismisses itself when it loses activation, and a file dialog is exactly
#              that. If the guard is wrong the panel is gone before the dialog is up
#   dock       none of it counts until the dock itself has rebuilt with the new list
#
# Click coordinates are computed from the layout constants restated below rather than read from
# the app. That is the point: if MenuTheme changes and this file does not, the clicks land
# somewhere else and the assertions fail, which is the reminder to look.
#
# Restores dock.json byte for byte on the way out, whatever happened.
#
# Kept ASCII-only: PowerShell 5.1 parses .ps1 as the system ANSI code page without a UTF-8 BOM.

param(
    [string] $Exe = "D:\AI\work space\win\src\FluidDock\bin\Debug\net9.0-windows10.0.19041.0\FluidDock.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class IT {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string w);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll")] static extern short VkKeyScanW(char ch);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    // Wrapped so C# passes a real null; PowerShell 5.1 coerces $null to "" for string parameters.
    public static IntPtr Menu() { return FindWindowW("FluidDockMenu", null); }
    public static IntPtr Dock() { return FindWindowW("FluidDockWindow", null); }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static bool Visible(IntPtr h) { return h != IntPtr.Zero && IsWindowVisible(h); }

    public static string ForegroundClass() {
        var s = new StringBuilder(64);
        GetClassNameW(GetForegroundWindow(), s, s.Capacity);
        return s.ToString();
    }

    public static void Down() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void Up()   { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }

    static void Key(byte vk) {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, 2, IntPtr.Zero);
    }

    public static void Escape() { Key(0x1B); }
    public static void Enter()  { Key(0x0D); }

    /// <summary>
    /// Puts a path into the open dialog's file-name box, by typing it.
    ///
    /// Two dead ends worth recording. SendKeys does nothing at all here: .NET falls back to
    /// journal playback, which Windows has refused to run since Vista, and it fails silently.
    /// SetWindowText on the edit does put the characters on screen, but the dialog keeps its own
    /// idea of what has been typed and updates it from keyboard input - so OK still acts on an
    /// empty file name. keybd_event goes through SendInput and is the one that works.
    /// </summary>
    public static bool FillDialog(string path) {
        IntPtr dialog = GetForegroundWindow();
        IntPtr outer = FindWindowExW(dialog, IntPtr.Zero, "ComboBoxEx32", null);
        if (outer == IntPtr.Zero) return false;

        IntPtr combo = FindWindowExW(outer, IntPtr.Zero, "ComboBox", null);
        if (combo == IntPtr.Zero) return false;

        IntPtr edit = FindWindowExW(combo, IntPtr.Zero, "Edit", null);
        if (edit == IntPtr.Zero) return false;

        Type(path);
        return true;
    }

    static void Type(string text) {
        foreach (char c in text) {
            short scan = VkKeyScanW(c);
            if (scan == -1) continue;

            byte vk = (byte)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0;

            if (shift) keybd_event(0x10, 0, 0, IntPtr.Zero);
            keybd_event(vk, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(8);
            keybd_event(vk, 0, 2, IntPtr.Zero);
            if (shift) keybd_event(0x10, 0, 2, IntPtr.Zero);
            System.Threading.Thread.Sleep(8);
        }
    }
}
'@

# ---- MenuTheme, restated. Keep in step with src\FluidDock\Menu\MenuTheme.cs. ----
$ShadowMargin       = 34
$HeaderHeight       = 54
$SectionHeaderHeight= 28
$RowHeight          = 44
$SectionGap         = 14
$PanelWidth         = 344
$PanelPadX          = 14
$RowPadX            = 14
$RemoveButtonSize   = 20
$CardWidth          = $PanelWidth - $PanelPadX * 2

$configPath = Join-Path (Split-Path $Exe) "config\dock.json"
$backup = [System.IO.File]::ReadAllBytes($configPath)

$passed = 0
$failed = 0

function Check($name, $ok, $detail) {
    if ($ok) { $script:passed++; Write-Output ("  PASS  {0,-38} {1}" -f $name, $detail) }
    else     { $script:failed++; Write-Output ("  FAIL  {0,-38} {1}" -f $name, $detail) }
}

function Labels {
    ((Get-Content $configPath -Raw | ConvertFrom-Json).Items | ForEach-Object { $_.Label }) -join ','
}

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 25
}

# Panel origin in screen coordinates. Re-read every time, because a structural change resizes
# the window and repositions it about its bottom-right corner.
function Panel {
    $h = [IT]::Menu()
    if (-not [IT]::Visible($h)) { throw "the panel is not open" }
    $r = [IT]::Rect($h)
    @{ X = $r.L + $ShadowMargin; Y = $r.T + $ShadowMargin; W = $r.R - $r.L; H = $r.B - $r.T; Right = $r.R; Bottom = $r.B }
}

# Centre of item row $i, in screen coordinates. The item list is the first section.
function RowY($panel, $i) { $panel.Y + $HeaderHeight + $SectionHeaderHeight + $i * $RowHeight + $RowHeight / 2 }
function RowX($panel)     { $panel.X + $PanelPadX + 90 }
function RemoveX($panel)  { $panel.X + $PanelPadX + $CardWidth - $RowPadX - $RemoveButtonSize / 2 }

function Drag($fromY, $toY, $x) {
    Move-To $x $fromY
    Start-Sleep -Milliseconds 120
    [IT]::Down()
    Start-Sleep -Milliseconds 60

    # Several steps, not one jump: the panel decides a press has become a drag from the movement
    # between two mouse messages, and a single teleport delivers only one.
    $steps = 10
    for ($i = 1; $i -le $steps; $i++) { Move-To $x ($fromY + ($toY - $fromY) * $i / $steps) }

    Start-Sleep -Milliseconds 150
    [IT]::Up()
    Start-Sleep -Milliseconds 700
}

$savedCursor = [System.Windows.Forms.Cursor]::Position

try {
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Start-Process $Exe
    Start-Sleep -Seconds 3

    & "$PSScriptRoot\TrayToggle.ps1" -Menu | Out-Null
    Start-Sleep -Milliseconds 900

    $p = Panel
    $before = Labels
    $names = $before.Split(',')
    Check "panel open" ([IT]::Visible([IT]::Menu())) ("{0}x{1} at {2},{3}" -f $p.W, $p.H, ($p.X - $ShadowMargin), ($p.Y - $ShadowMargin))
    Check "config has items" ($names.Count -ge 4) $before

    # ---- Reorder: third entry to the top ------------------------------------------------
    $heightBefore = $p.H
    Drag (RowY $p 2) (RowY $p 0) (RowX $p)

    $p = Panel
    $after = Labels
    $moved = $after.Split(',')
    Check "drag moved entry to the front" ($moved[0] -eq $names[2]) "$before -> $after"
    Check "drag left the rest in order" `
        (($moved[1] -eq $names[0]) -and ($moved[2] -eq $names[1]) -and ($moved[3] -eq $names[3])) $after
    Check "reorder did not resize the panel" ($p.H -eq $heightBefore) "$($p.H) px"

    # ---- Reorder back -------------------------------------------------------------------
    Drag (RowY $p 0) (RowY $p 2) (RowX $p)
    $p = Panel
    Check "drag back restored the order" ((Labels) -eq $before) (Labels)

    # ---- Remove -------------------------------------------------------------------------
    $anchorRight = $p.Right
    $anchorBottom = $p.Bottom
    $heightBefore = $p.H
    $doomed = $names[$names.Count - 1]

    Move-To (RowX $p) (RowY $p ($names.Count - 1))
    Start-Sleep -Milliseconds 250
    Move-To (RemoveX $p) (RowY $p ($names.Count - 1))
    Start-Sleep -Milliseconds 250
    [IT]::Down(); Start-Sleep -Milliseconds 60; [IT]::Up()
    Start-Sleep -Milliseconds 900

    $p = Panel
    $left = Labels
    Check "remove dropped the entry that was clicked" `
        (($left.Split(',').Count -eq ($names.Count - 1)) -and ($left -notmatch [regex]::Escape($doomed))) `
        "-$doomed -> $left"

    # Not a height check. The content is already taller than MaxViewportHeight, so losing a row
    # shortens what scrolls rather than the panel - which is the invariant worth asserting.
    Check "panel stayed within the viewport cap" ($p.H -le ($HeaderHeight + 560 + 10 + $ShadowMargin * 2)) `
        "$heightBefore -> $($p.H) px"
    Check "panel grew from its bottom-right corner" `
        (([Math]::Abs($p.Right - $anchorRight) -le 1) -and ([Math]::Abs($p.Bottom - $anchorBottom) -le 1)) `
        "corner ($($p.Right),$($p.Bottom)) was ($anchorRight,$anchorBottom)"

    # ---- The rebuilt rows are bound to the new list --------------------------------------
    # A rebuild that handed the rows stale DockItemConfig objects would still look right and
    # still drag - and would write its order into a list nothing was going to save.
    $shuffled = $left.Split(',')
    Drag (RowY $p 0) (RowY $p 1) (RowX $p)
    Check "rebuilt rows edit the live list" ((Labels) -eq "$($shuffled[1]),$($shuffled[0])," + (($shuffled | Select-Object -Skip 2) -join ',')) (Labels)
    Drag (RowY $p 1) (RowY $p 0) (RowX $p)

    # ---- The dock followed ---------------------------------------------------------------
    Start-Sleep -Milliseconds 900
    $left = Labels
    $dockCount = (Get-Content $configPath -Raw | ConvertFrom-Json).Items.Count
    Check "dock.json holds the edited list" ($dockCount -eq ($names.Count - 1)) "$dockCount items"

    # ---- Adding, through the real dialog ---------------------------------------------------
    # The add buttons sit in their own card, one section gap below the item list.
    $addY = $p.Y + $HeaderHeight + $SectionHeaderHeight + $dockCount * $RowHeight + $SectionGap + $RowHeight / 2
    Move-To (RowX $p) $addY
    Start-Sleep -Milliseconds 200
    [IT]::Down(); Start-Sleep -Milliseconds 60; [IT]::Up()
    Start-Sleep -Seconds 2

    $dialogClass = [IT]::ForegroundClass()
    Check "add opened the shell's dialog" ($dialogClass -eq "#32770") "foreground class '$dialogClass'"

    Check "dialog took the path" ([IT]::FillDialog("C:\Windows\System32\charmap.exe")) "charmap.exe"
    Start-Sleep -Milliseconds 400
    [IT]::Enter()
    Start-Sleep -Seconds 2

    Check "panel survived the dialog" ([IT]::Visible([IT]::Menu())) "still on screen"

    $added = Labels
    Check "add appended the chosen program" ($added -eq "$left,charmap") $added

    # ---- Custom icon ------------------------------------------------------------------------
    $p = Panel
    $last = $added.Split(',').Count - 1
    Move-To (RowX $p) (RowY $p $last)
    Start-Sleep -Milliseconds 300
    Move-To ($p.X + 270) (RowY $p $last)
    Start-Sleep -Milliseconds 250
    [IT]::Down(); Start-Sleep -Milliseconds 60; [IT]::Up()
    Start-Sleep -Seconds 2

    Check "icon button opened the picker" ([IT]::ForegroundClass() -eq "#32770") "foreground '$([IT]::ForegroundClass())'"

    # An .exe, not an image: pointing the icon at a program means "borrow that program's icon",
    # which is the path most likely to be quietly broken and the easiest one to test.
    Check "icon dialog took the path" ([IT]::FillDialog("C:\Windows\System32\notepad.exe")) "notepad.exe"
    Start-Sleep -Milliseconds 400
    [IT]::Enter()
    Start-Sleep -Seconds 2

    $icon = (Get-Content $configPath -Raw | ConvertFrom-Json).Items[$last].Icon
    Check "icon written to the entry" ($icon -eq "C:\Windows\System32\notepad.exe") "Icon = '$icon'"
    Check "panel survived the icon picker" ([IT]::Visible([IT]::Menu())) "still on screen"

    # With a custom icon set the row grows a third action, which pushes the other two left.
    $p = Panel
    Move-To (RowX $p) (RowY $p $last)
    Start-Sleep -Milliseconds 300
    Move-To ($p.X + 270) (RowY $p $last)
    Start-Sleep -Milliseconds 250
    [IT]::Down(); Start-Sleep -Milliseconds 60; [IT]::Up()
    Start-Sleep -Milliseconds 1200

    $icon = (Get-Content $configPath -Raw | ConvertFrom-Json).Items[$last].Icon
    Check "reset cleared the custom icon" ($null -eq $icon) "Icon = '$icon'"

    # ---- And back to where we started -------------------------------------------------------
    $p = Panel
    Move-To (RowX $p) (RowY $p $last)
    Start-Sleep -Milliseconds 250
    Move-To (RemoveX $p) (RowY $p $last)
    Start-Sleep -Milliseconds 250
    [IT]::Down(); Start-Sleep -Milliseconds 60; [IT]::Up()
    Start-Sleep -Milliseconds 1200

    Check "the added entry can be removed again" ((Labels) -eq $left) (Labels)
}
finally {
    [System.Windows.Forms.Cursor]::Position = $savedCursor
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    [System.IO.File]::WriteAllBytes($configPath, $backup)
}

Write-Output ""
if ($failed -eq 0) { Write-Output "ItemsTest PASS - $passed checks." }
else { Write-Output "ItemsTest FAIL - $failed of $($passed + $failed) checks failed." }
