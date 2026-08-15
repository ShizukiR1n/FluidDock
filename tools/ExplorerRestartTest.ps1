# What survives an Explorer restart.
#
# The dock is a child of Progman, and Explorer owns Progman. When Explorer goes down it takes
# every child with it, so the dock's window should die even though the process lives on. The tray
# icon is deliberately on its own top-level window for exactly this reason: it should survive, and
# its TaskbarCreated handler should put the icon back once the new Explorer publishes the message.
#
# This has never been run before, so it is written to distinguish outcomes rather than to assert
# one. Three things can independently be alive or dead - the process, the dock window, and the
# tray icon - and "the dock is gone" reads the same from outside whether the process crashed or
# merely lost its window.
#
# Restarting Explorer closes any open File Explorer windows. Other applications are unaffected.

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ER {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    // GetAncestor, not GetParent. The dock is a WS_POPUP that has been SetParent'd into Progman,
    // and GetParent returns the *owner* rather than the parent for anything without WS_CHILD -
    // so it reports 0 for a window that demonstrably has a parent. GA_PARENT (1) gives the real
    // one, which is the whole point of the column: it is what the dock dies along with.
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public static IntPtr Parent(IntPtr h) { return GetAncestor(h, 1); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);

    // Wrapped so null is passed from C#. Calling FindWindowW($null, ...) from PowerShell passes
    // "" instead - PowerShell coerces $null for a string parameter - and FindWindowW matches
    // nothing against an empty title. The first run of this test reported the tray window as gone
    // while the tray icon was demonstrably registered, which is what that bug looks like.
    public static IntPtr TrayWindow() { return FindWindowW("FluidDockTray", null); }

    [DllImport("user32.dll")] static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    public static void CtrlAltQ() {
        keybd_event(0x11,0,0,IntPtr.Zero); keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x51,0,0,IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(0x51,0,2,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero); keybd_event(0x11,0,2,IntPtr.Zero);
    }
}
'@

function State($label) {
    $proc = @(Get-Process FluidDock -ErrorAction SilentlyContinue)
    $tray = [ER]::TrayWindow()

    # Resolved fresh every time: after a restart the old handle is meaningless, and asking whether
    # the *old* handle is still valid is a different question from whether a dock exists now.
    $dock = [IntPtr]::Zero
    try { $dock = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1" 2>$null) } catch { }

    $icon = $false
    try { $icon = (& "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow).Found } catch { }

    [pscustomobject]@{
        Label   = $label
        Pid     = if ($proc.Count) { $proc[0].Id } else { 0 }
        Dock    = $dock
        Visible = if ($dock -ne [IntPtr]::Zero) { [ER]::IsWindowVisible($dock) } else { $false }
        Parent  = if ($dock -ne [IntPtr]::Zero) { [ER]::Parent($dock) } else { [IntPtr]::Zero }
        TrayWnd = $tray
        Icon    = $icon
    }
}

function Show($s) {
    "  {0,-16} pid={1,-7} dock=0x{2:X}{3} parent=0x{4:X}  trayWnd=0x{5:X}  iconInTray={6}" -f `
        $s.Label, $s.Pid, [int64]$s.Dock, $(if ($s.Dock -ne [IntPtr]::Zero) { " visible=$($s.Visible)" } else { "" }),
        [int64]$s.Parent, [int64]$s.TrayWnd, $s.Icon
}

$before = State "before"
Show $before
if ($before.Pid -eq 0) { Write-Output "FluidDock is not running - nothing to test"; return }
$oldDock = $before.Dock
$oldPid = $before.Pid

Write-Output ""
Write-Output "restarting Explorer..."
Stop-Process -Name explorer -Force
Start-Sleep -Seconds 3

# Windows normally relaunches Explorer by itself; start it only if it did not.
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
    Write-Output "  it did not come back on its own - starting it"
    Start-Process explorer.exe
}
# The shell needs to be far enough along to have published TaskbarCreated and rebuilt the tray.
Start-Sleep -Seconds 12

Write-Output ""
$after = State "after"
Show $after

Write-Output ""
Write-Output "what happened:"
Write-Output ("  process        {0}" -f $(
    if ($after.Pid -eq 0) { "DIED" }
    elseif ($after.Pid -eq $oldPid) { "survived (same pid $oldPid)" }
    else { "restarted?? old $oldPid, now $($after.Pid)" }))

Write-Output ("  old dock hwnd  {0}" -f $(if ([ER]::IsWindow($oldDock)) { "still a window" } else { "destroyed" }))
Write-Output ("  dock now       {0}" -f $(
    if ($after.Dock -eq [IntPtr]::Zero) { "GONE" }
    elseif ($after.Dock -eq $oldDock) { "same window, survived" }
    else { "rebuilt as a new window" }))
Write-Output ("  tray window    {0}" -f $(if ($after.TrayWnd -ne [IntPtr]::Zero) { "alive" } else { "gone" }))
Write-Output ("  tray icon      {0}" -f $(if ($after.Icon) { "re-registered" } else { "MISSING" }))

# The exit routes matter more than the dock itself. A dock that vanished is an annoyance; a
# process that cannot be closed from either of its own exit paths, still holding a tray icon,
# is something the user can only deal with through Task Manager.
if ($after.Pid -ne 0) {
    Write-Output ""
    Write-Output "can the user still get rid of it?"

    [ER]::CtrlAltQ()
    Start-Sleep -Seconds 3
    $afterHotkey = @(Get-Process FluidDock -ErrorAction SilentlyContinue).Count
    Write-Output ("  Ctrl+Alt+Q     {0}" -f $(if ($afterHotkey -eq 0) { "worked" } else { "DEAD - hotkeys were registered on the dock window" }))

    if ($afterHotkey -ne 0) {
        try {
            & "$PSScriptRoot\TrayToggle.ps1" -Exit
            Start-Sleep -Seconds 3
            $afterExit = @(Get-Process FluidDock -ErrorAction SilentlyContinue).Count
            Write-Output ("  tray Exit      {0}" -f $(if ($afterExit -eq 0) { "worked" } else { "DEAD - it posts WM_CLOSE to the destroyed dock window" }))
            if ($afterExit -ne 0) { Write-Output "  -> Task Manager is the only way out" }
        } catch {
            Write-Output "  tray Exit      could not be driven: $($_.Exception.Message)"
        }
    }
}
