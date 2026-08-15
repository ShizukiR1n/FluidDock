# Explorer restart: does the dock come back, and can the app still be closed?
#
# The dock is a child of Progman, and Explorer owns Progman, so restarting Explorer destroys the
# dock's window while the process carries on running. That is not a hypothetical - it happens on
# every Explorer crash, and it used to leave a process with a tray icon, no window, and no working
# way out: exit posted WM_CLOSE to the destroyed dock window, the hotkey had been registered on
# that same window, and Task Manager was the only remaining option.
#
# So this test asks three things, in order of how much they matter:
#   1. can the user still close it            - the floor, and the thing that was broken
#   2. does the dock come back on its own     - the actual fix
#   3. is what came back a working dock       - a window in the right place that does not
#                                               magnify is not a dock, it is a screenshot
#
# (3) is measured as a whole-frame difference between a resting shot and a hovering one. Pixel
# thresholds were tried twice and failed twice, both times in the measurement rather than the
# dock: CopyFromScreen returns a fully opaque bitmap so alpha says nothing, and a saturation
# threshold assumes a grey desktop behind the dock. A difference against the dock's own resting
# frame does not care what the wallpaper looks like.
#
# The order is deliberate: exit is tested last because it ends the process. The dock is relaunched
# at the end from the path it was running from, so the test leaves the machine as it found it.
#
# Restarting Explorer closes any open File Explorer windows. Other applications are unaffected.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class ER {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    // GetAncestor, not GetParent. The dock is a WS_POPUP that has been SetParent'd into Progman,
    // and GetParent returns the *owner* rather than the parent for anything without WS_CHILD -
    // so it reports 0 for a window that demonstrably has one. GA_PARENT (1) gives the real
    // parent, which is the whole point of the column: it is what the dock dies along with.
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public static IntPtr Parent(IntPtr h) { return GetAncestor(h, 1); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    public static string ClassOf(IntPtr h) {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(64);
        GetClassNameW(h, sb, 64);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);

    // Wrapped so null is passed from C#. Calling FindWindowW($null, ...) from PowerShell passes
    // "" instead - PowerShell coerces $null for a string parameter - and FindWindowW matches
    // nothing against an empty title. An earlier run of this test reported the tray window as
    // gone while the tray icon was demonstrably registered, which is what that bug looks like.
    public static IntPtr TrayWindow() { return FindWindowW("FluidDockTray", null); }

    /// Pixels differing by more than a tolerance between two frames. The tolerance absorbs the
    /// compositor's dithering of the tint layer, which flickers the bottom bit or two of a
    /// channel between otherwise identical frames.
    public static int Diff(Bitmap a, Bitmap b, int tolerance) {
        byte[] ba = Bytes(a), bb = Bytes(b);
        if (ba.Length != bb.Length) return int.MaxValue;
        int count = 0;
        for (int i = 0; i < ba.Length; i += 4) {
            if (Math.Abs(ba[i]   - bb[i])   > tolerance ||
                Math.Abs(ba[i+1] - bb[i+1]) > tolerance ||
                Math.Abs(ba[i+2] - bb[i+2]) > tolerance) count++;
        }
        return count;
    }

    static byte[] Bytes(Bitmap bmp) {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[d.Stride * bmp.Height];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(d);
        return buf;
    }
}
'@ -ReferencedAssemblies System.Drawing

$failures = 0

# A magnified icon moves tens of thousands of pixels; two resting frames differ by a couple of
# hundred at most. Anything between the two is not a number this test has to interpret.
$MinLift = 2000
$Away = @(300, 500)

function Move-To($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$x), ([int]$y)
    Start-Sleep -Milliseconds 400
}

function Shot($rect) {
    $bmp = New-Object System.Drawing.Bitmap ($rect.R - $rect.L), ($rect.B - $rect.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.L, $rect.T, 0, 0, $bmp.Size)
    $g.Dispose()
    return $bmp
}

# How much the dock changes when the cursor is put on its middle icon. Zero means it is a picture.
function Measure-Magnification($hwnd) {
    $rect = New-Object ER+RECT
    if (-not [ER]::GetWindowRect($hwnd, [ref]$rect)) { return -1 }

    Move-To $Away[0] $Away[1]
    $rest = Shot $rect

    # Middle of the dock horizontally is the middle icon with an odd icon count, and the icon row
    # sits just above the window's bottom edge - the window carries shadow margin below it.
    # Measured on the 632x186 window: icon centres at y = bottom - 44.
    Move-To (($rect.L + $rect.R) / 2) ($rect.B - 44)
    $hover = Shot $rect

    $lift = [ER]::Diff($rest, $hover, 12)
    $rest.Dispose(); $hover.Dispose()
    Move-To $Away[0] $Away[1]
    return $lift
}

function State($label) {
    $proc = @(Get-Process FluidDock -ErrorAction SilentlyContinue)

    # Resolved fresh every time: after a restart the old handle is meaningless, and asking whether
    # the *old* handle is still valid is a different question from whether a dock exists now.
    $dock = [IntPtr]::Zero
    try { $dock = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1" 2>$null) } catch { }

    $icon = $false
    try { $icon = (& "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow).Found } catch { }

    $parent = if ($dock -ne [IntPtr]::Zero) { [ER]::Parent($dock) } else { [IntPtr]::Zero }

    [pscustomobject]@{
        Label       = $label
        Pid         = if ($proc.Count) { $proc[0].Id } else { 0 }
        Path        = if ($proc.Count) { $proc[0].Path } else { "" }
        Dock        = $dock
        Visible     = if ($dock -ne [IntPtr]::Zero) { [ER]::IsWindowVisible($dock) } else { $false }
        Parent      = $parent
        ParentClass = [ER]::ClassOf($parent)
        TrayWnd     = [ER]::TrayWindow()
        Icon        = $icon
    }
}

function Show($s) {
    "  {0,-7} pid={1,-7} dock=0x{2:X}{3} parent=0x{4:X} ({5})  trayWnd=0x{6:X}  iconInTray={7}" -f `
        $s.Label, $s.Pid, [int64]$s.Dock,
        $(if ($s.Dock -ne [IntPtr]::Zero) { " visible=$($s.Visible)" } else { "" }),
        [int64]$s.Parent, $s.ParentClass, [int64]$s.TrayWnd, $s.Icon
}

$saved = [System.Windows.Forms.Cursor]::Position

try {
    $before = State "before"
    Show $before
    if ($before.Pid -eq 0) { Write-Output "FluidDock is not running - nothing to test"; return }

    $oldDock = $before.Dock
    $oldPid = $before.Pid
    $exePath = $before.Path

    $liftBefore = Measure-Magnification $oldDock
    Write-Output ("  baseline hover: {0} px changed" -f $liftBefore)
    if ($liftBefore -lt $MinLift) {
        Write-Output "  FAIL - the dock was not magnifying before the restart, so nothing below means anything"
        $failures++
    }

    # --- restart the shell -----------------------------------------------------------------------
    Write-Output ""
    Write-Output "restarting Explorer..."
    Stop-Process -Name explorer -Force

    # Windows normally relaunches Explorer by itself; start it only if it did not.
    Start-Sleep -Seconds 3
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
        Write-Output "  it did not come back on its own - starting it"
        Start-Process explorer.exe
    }

    # Poll rather than sleep a fixed amount. The dock waits for the desktop's icon view before
    # parenting into it, so how long this takes is the shell's business, not a constant we know.
    $deadline = (Get-Date).AddSeconds(30)
    $newDock = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        try { $newDock = [IntPtr](& "$PSScriptRoot\DockHwnd.ps1" 2>$null) } catch { $newDock = [IntPtr]::Zero }
        if ($newDock -ne [IntPtr]::Zero -and $newDock -ne $oldDock -and [ER]::Parent($newDock) -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2   # let the tray finish rebuilding before looking for the icon

    Write-Output ""
    $after = State "after"
    Show $after

    # --- what happened ---------------------------------------------------------------------------
    Write-Output ""
    Write-Output "what happened:"
    Write-Output ("  process        {0}" -f $(
        if ($after.Pid -eq 0) { "DIED" }
        elseif ($after.Pid -eq $oldPid) { "survived (same pid $oldPid)" }
        else { "restarted?? old $oldPid, now $($after.Pid)" }))
    if ($after.Pid -ne $oldPid) { $failures++ }

    Write-Output ("  old dock hwnd  {0}" -f $(if ([ER]::IsWindow($oldDock)) { "still a window" } else { "destroyed" }))

    Write-Output ("  dock now       {0}" -f $(
        if ($after.Dock -eq [IntPtr]::Zero) { "GONE" }
        elseif ($after.Dock -eq $oldDock) { "same window, survived" }
        else { "rebuilt as a new window" }))
    if ($after.Dock -eq [IntPtr]::Zero) { Write-Output "  FAIL - the dock did not come back"; $failures++ }

    # A rebuilt dock that is not a child of the desktop is a dock floating over the user's
    # windows: it looks right in a screenshot and is wrong in use.
    Write-Output ("  back on the    {0}" -f $(
        if ($after.ParentClass -eq "Progman" -or $after.ParentClass -eq "WorkerW") { "desktop layer ($($after.ParentClass))" }
        elseif ($after.Dock -eq [IntPtr]::Zero) { "n/a" }
        else { "WRONG LAYER - parent is '$($after.ParentClass)'" }))
    if ($after.Dock -ne [IntPtr]::Zero -and $after.ParentClass -notin @("Progman", "WorkerW")) { $failures++ }

    if ($after.Dock -ne [IntPtr]::Zero -and -not $after.Visible) {
        Write-Output "  FAIL - the rebuilt dock is not visible"; $failures++
    }

    Write-Output ("  tray window    {0}" -f $(if ($after.TrayWnd -ne [IntPtr]::Zero) { "alive" } else { "gone" }))
    if ($after.TrayWnd -eq [IntPtr]::Zero) { $failures++ }

    Write-Output ("  tray icon      {0}" -f $(if ($after.Icon) { "re-registered" } else { "MISSING" }))
    if (-not $after.Icon) { $failures++ }

    # --- is it a working dock or just a window? --------------------------------------------------
    if ($after.Dock -ne [IntPtr]::Zero) {
        $liftAfter = Measure-Magnification $after.Dock
        Write-Output ("  magnifies      {0} px changed on hover" -f $liftAfter)
        if ($liftAfter -lt $MinLift) {
            Write-Output "  FAIL - the window came back but the dock inside it is not responding"
            $failures++
        }
    }

    # --- and can it still be closed? -------------------------------------------------------------
    # Tested last, because it ends the process. There is no hotkey to check any more: Ctrl+Alt+Q
    # was removed, which also removes the half of the old failure that came from registering it on
    # a window Explorer can destroy. Exit through the tray is now the only route, so it is the only
    # one that has to work.
    Write-Output ""
    Write-Output "can the user still get rid of it?"
    try {
        & "$PSScriptRoot\TrayToggle.ps1" -Exit
        Start-Sleep -Seconds 3
        $left = @(Get-Process FluidDock -ErrorAction SilentlyContinue).Count
        Write-Output ("  tray Exit      {0}" -f $(if ($left -eq 0) { "worked" } else { "DEAD - Task Manager is the only way out" }))
        if ($left -ne 0) { $failures++ }

        if ($left -eq 0) {
            $stale = & "$PSScriptRoot\TrayButtons.ps1" -OpenOverflow
            Write-Output ("  tray icon      {0}" -f $(if ($stale.Found) { "STILL THERE - it was not removed on exit" } else { "removed" }))
            if ($stale.Found) { $failures++ }
        }
    }
    catch {
        Write-Output "  tray Exit      could not be driven: $($_.Exception.Message)"
        $failures++
    }

    # --- put it back -----------------------------------------------------------------------------
    if ($exePath -and -not (Get-Process FluidDock -ErrorAction SilentlyContinue)) {
        Start-Process $exePath
        Start-Sleep -Seconds 3
        Write-Output ("  relaunched     {0}" -f $exePath)
    }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output ""
if ($failures -eq 0) { Write-Output "PASS - the dock rebuilds itself and exit still works." }
else { Write-Output "FAIL - $failures problem(s)." }
