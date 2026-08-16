# What the settings panel actually costs, and whether opening it repeatedly leaks.
#
# This script exists because of a wrong answer that survived for a long time. Opening the panel
# for the first time cost about 40 MB of private memory and never gave it back, and that was
# written down as the price of the tree - a page of rasterised strings, a background bitmap, a
# list of icons - with the font stack named as the largest part. Both halves were wrong, and the
# DLL column below is what settles it:
#
#   before any open   46.6 MB    99 DLLs
#   open  1           52.6 MB   105 DLLs
#   open  3           94.1 MB   131 DLLs   <- +37 MB arriving with twenty DLLs
#
# The twenty are Sogou's: SogouTSF.ime, SogouPY.ime, PicFace64.dll, ai_voice_input_bundle64.dll
# and friends. Windows attaches the user's IME to a process the first time one of its windows
# takes the keyboard focus, and the settings panel takes focus deliberately - that is how it
# dismisses when you click away. Nothing in this program accepts typed text, so Program.Main now
# calls ImmDisableIME and the whole 37 MB never arrives. Rerun with that call commented out and
# the jump comes straight back.
#
# What is left is the panel's own share, and it is small:
#
#                     closed     reopen
#   panel kept        52.7 MB      5 ms
#   panel destroyed   57.7 MB     39 ms
#
# Which is why the panel is still kept. Destroying it on close was implemented, measured, and
# came out worse on both axes: every rebuild churns thirty-odd bitmaps and several hundred
# Composition objects through the managed heap, and the heap grows to hold the churn faster than
# a forced collection gives it back.
#
# So the number this still guards is the third one - a cycle that ends higher than the one before
# it. Opening and closing the same panel repeatedly is exactly the shape of test that finds a
# tree which is not being let go, and the panel holds GPU surfaces, GDI bitmaps and Composition
# objects that each have their own way of not being released.
#
# Nothing here touches the mouse or the keyboard. The panel is opened and closed by sending the
# tray icon's own double-click message, which is the same route TrayIcon takes to MenuRequested -
# so the app cannot tell this from a user, and the user's cursor and focus are left alone. Sent
# rather than posted, deliberately: SendMessage blocks until the window procedure returns, so the
# stopwatch around it covers the whole of the build.
#
# This changes nothing on disk.
#
# Kept ASCII-only: PowerShell 5.1 parses .ps1 as the system ANSI code page without a UTF-8 BOM.

param(
    [int] $Cycles = 4,
    [string] $Exe = "D:\AI\work space\win\src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe"
)

$ErrorActionPreference = "Stop"

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MM {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string w);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint ms, out IntPtr r);

    // GDI and USER handle counts. Bitmaps, fonts and device contexts are the panel's largest
    // non-managed cost after the surfaces, and they are invisible to PrivateMemorySize64 - a
    // tree that leaked every one of them would show up here and nowhere else.
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr h, uint flags);

    // Wrapped so C# passes a real null; PowerShell 5.1 coerces $null to "" for string parameters.
    public static IntPtr Tray() { return FindWindowW("FluidDockTray", null); }
    public static IntPtr Menu() { return FindWindowW("FluidDockMenu", null); }
    public static bool Visible(IntPtr h) { return h != IntPtr.Zero && IsWindowVisible(h); }

    public static uint Gdi(IntPtr proc)  { return GetGuiResources(proc, 0); }
    public static uint User(IntPtr proc) { return GetGuiResources(proc, 1); }

    /// WM_APP_TRAY carrying WM_LBUTTONDBLCLK, which is the tray icon's "open the settings panel".
    /// SMTO_ABORTIFHUNG so a wedged UI thread fails the test rather than hanging it.
    public static bool Toggle(IntPtr tray) {
        IntPtr r;
        return SendMessageTimeoutW(tray, 0x8003, new IntPtr(1), new IntPtr(0x0203), 0x0002, 15000, out r) != IntPtr.Zero;
    }
}
'@

if (-not (Get-Process FluidDock -ErrorAction SilentlyContinue)) {
    Start-Process $Exe
    Start-Sleep -Seconds 3
}
$process = Get-Process FluidDock | Select-Object -First 1

$tray = [MM]::Tray()
if ($tray -eq [IntPtr]::Zero) { throw "no FluidDockTray window - is the dock running?" }

$rows = @()
$openMs = @()

function Snapshot($label) {
    $process.Refresh()
    $script:rows += [PSCustomObject]@{
        Phase   = $label
        Private = $process.PrivateMemorySize64 / 1MB
        Working = $process.WorkingSet64 / 1MB
        Handles = $process.HandleCount
        Gdi     = [MM]::Gdi($process.Handle)
        User    = [MM]::User($process.Handle)
        # The discriminator that decides whether any of this is reclaimable at all. Memory that
        # arrives alongside a batch of newly loaded DLLs is those DLLs and the caches they build -
        # font stacks, shell extensions - and none of it unloads when our tree is released.
        Modules = $process.Modules.Count
    }
}

# Long enough for the 130ms close fade to finish, the WM_APP_MENU_FREE it posts to be dispatched,
# and the forced collection inside it to run. Measuring earlier than this measures the wait, not
# the result.
function Wait-Settled { Start-Sleep -Milliseconds 900 }

if ([MM]::Visible([MM]::Menu())) {
    [void][MM]::Toggle($tray)
    Wait-Settled
}

Wait-Settled
Snapshot "before any open"

for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not [MM]::Toggle($tray)) { throw "the app did not answer the open on cycle $cycle" }
    $watch.Stop()
    $script:openMs += $watch.Elapsed.TotalMilliseconds

    Start-Sleep -Milliseconds 500
    if (-not [MM]::Visible([MM]::Menu())) { throw "the panel did not open on cycle $cycle" }
    Snapshot "open  $cycle"

    [void][MM]::Toggle($tray)
    Wait-Settled
    if ([MM]::Visible([MM]::Menu())) { throw "the panel did not close on cycle $cycle" }
    Snapshot "closed $cycle"
}

$rows | Format-Table `
    @{ L = "phase";       E = { $_.Phase } },
    @{ L = "private MB";  E = { "{0,6:N1}" -f $_.Private } },
    @{ L = "working MB";  E = { "{0,6:N1}" -f $_.Working } },
    @{ L = "handles";     E = { $_.Handles } },
    @{ L = "GDI";         E = { $_.Gdi } },
    @{ L = "USER";        E = { $_.User } },
    @{ L = "DLLs";        E = { $_.Modules } } | Out-String | Write-Output

$before  = $rows[0].Private
$open1   = ($rows | Where-Object { $_.Phase -eq "open  1" }).Private
$closed1 = ($rows | Where-Object { $_.Phase -eq "closed 1" }).Private
$closedN = ($rows | Where-Object { $_.Phase -like "closed*" } | Select-Object -Last 1).Private

$cost     = $open1 - $before
$returned = $open1 - $closed1
$drift    = $closedN - $closed1

Write-Output ("first open cost      {0,6:N1} MB" -f $cost)
Write-Output ("drift over $Cycles cycles   {0,6:N1} MB" -f $drift)
Write-Output ("DLLs loaded          {0} -> {1}" -f $rows[0].Modules, $rows[-1].Modules)
Write-Output ""
Write-Output ("open took            {0}" -f (($openMs | ForEach-Object { "{0:N0} ms" -f $_ }) -join ", "))
Write-Output ""

# Two pass/fail bounds; everything above them is a report.
#
# Drift is the leak test. A tree that is genuinely not being let go costs the full first-open
# figure again on every cycle, so the bound is well under that - and well over the megabyte or so
# of heap the allocator hangs on to, which is normal and is not what this is looking for.
#
# The DLL count is the IME test, and it is here rather than in a comment because ImmDisableIME is
# a single line in Program.Main that nothing else in the program depends on. It is exactly the
# sort of line that gets moved below the first CreateWindowEx during some unrelated tidy-up, at
# which point it silently stops working and 37 MB comes back with no other symptom.
$failures = 0
if ($drift -gt 8) { $failures++; Write-Output ("FAIL - closed state grew {0:N1} MB across the run; something is not being released." -f $drift) }
if (($rows[-1].Modules - $rows[0].Modules) -gt 12) {
    $failures++
    Write-Output "FAIL - opening the panel pulled in a batch of DLLs. If they are *.ime and Sogou's,"
    Write-Output "       the ImmDisableIME call in Program.Main is no longer running before the"
    Write-Output "       first CreateWindowEx."
}

if ($failures -eq 0) { Write-Output "MenuMemoryTest PASS - repeated open/close is flat and no IME attached." }
else { Write-Output "MenuMemoryTest FAIL - $failures problem(s) above." }
