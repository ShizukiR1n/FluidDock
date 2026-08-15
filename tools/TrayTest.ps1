# Finds the FluidDock tray icon and reports where the shell put it.
#
# "Shell_NotifyIcon returned true" is not the same as "the user can see it". On Windows 10 a new
# icon goes into the hidden overflow flyout by default, not the visible tray, and the API reports
# success either way. UI Automation is the only way to tell the two apart from outside.
#
# Note the [Console]::WriteLine instead of Write-Output inside the functions: in PowerShell a
# function returns everything written to the output stream, so a Write-Output there would be
# captured into the caller's variable alongside the element. First version of this script did
# exactly that, and cheerfully reported a string as a "found" tray icon.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TrayWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
}
'@

$auto = [System.Windows.Automation.AutomationElement]

# Every descendant, not just ControlType.Button. The tray is a ToolbarWindow32 and its items do
# not come back as Buttons here - filtering on that returned zero elements from a tray that
# demonstrably had eleven icons in it.
$buttons = [System.Windows.Automation.Condition]::TrueCondition

function Find-TrayButton($label, $className) {
    $hwnd = [TrayWin]::FindWindowW($className, $null)
    if ($hwnd -eq [IntPtr]::Zero) {
        [Console]::WriteLine("  {0,-20} window not present", $label)
        return $null
    }

    $root = $auto::FromHandle($hwnd)
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttons)
    foreach ($b in $found) {
        if ($b.Current.Name -like "*FluidDock*") {
            $r = $b.Current.BoundingRectangle
            [Console]::WriteLine("  {0,-20} FOUND '{1}' at {2},{3}", $label, $b.Current.Name, [int]$r.X, [int]$r.Y)
            return $b
        }
    }
    [Console]::WriteLine("  {0,-20} not here ({1} buttons scanned)", $label, $found.Count)
    return $null
}

Write-Output "searching the notification area:"
$icon = Find-TrayButton "visible tray" "Shell_TrayWnd"
$where = "visible tray"
if (-not $icon) {
    $icon = Find-TrayButton "overflow flyout" "NotifyIconOverflowWindow"
    $where = "overflow flyout"
}

Write-Output ""
if ($icon) {
    $r = $icon.Current.BoundingRectangle
    Write-Output ("PASS - tray icon is in the {0}, centre {1},{2}" -f `
        $where, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
} else {
    Write-Output "FAIL - tray icon not found in either location"
}
