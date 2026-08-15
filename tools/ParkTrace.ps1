# Traces CPU in 2s buckets while the cursor sits still on the dock.
#
# A single 10s average cannot tell a settling transient apart from a slow leak - both show up as
# "some milliseconds". The shape does: a transient decays to zero and stays there, a leak holds
# a steady rate for as long as you care to watch.

Add-Type -AssemblyName System.Windows.Forms

$process = Get-Process FluidDock -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FluidDock is not running" }

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dockX = [int]($work.Width / 2)
$dockY = [int]($work.Bottom - 12 - 48 / 2)
$saved = [System.Windows.Forms.Cursor]::Position

$lines = New-Object System.Collections.ArrayList
try {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 40, 40
    Start-Sleep -Seconds 3
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $dockX, $dockY

    for ($i = 0; $i -lt 10; $i++) {
        $process.Refresh()
        $a = $process.TotalProcessorTime
        Start-Sleep -Seconds 2
        $process.Refresh()
        $ms = ($process.TotalProcessorTime - $a).TotalMilliseconds
        [void]$lines.Add(("  {0,2}-{1,2}s  {2,5:N0} ms  {3}" -f ($i * 2), ($i * 2 + 2), $ms, ('#' * [Math]::Min(40, [int]$ms))))
    }
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
}

Write-Output "cursor parked on the dock, CPU per 2s bucket:"
$lines | ForEach-Object { Write-Output $_ }
