# Compares packaging options on the three things that actually differ: file size, resident
# memory, and cold start.
#
# Compression looked like the obvious win at 57 MB against 149 MB, until the idle test showed the
# compressed build sitting at 171 MB of private bytes instead of 49 MB. A single-file bundle is
# mapped, not read - the runtime pages assemblies in from the file on demand and shares them with
# every other process using them. Compressed entries cannot be mapped, so they are decompressed
# onto the heap instead, private and unshareable. The disk saving is paid back in RAM, all day.

$builds = @(
    @{ Name = "dev (framework-dependent)"; Path = "D:\AI\work space\win\src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe" },
    @{ Name = "single-file, compressed";   Path = "D:\AI\work space\win\dist\FluidDock.exe" },
    @{ Name = "single-file, uncompressed"; Path = "D:\AI\work space\win\dist-nc\FluidDock.exe" },
    @{ Name = "uncompressed, no R2R";      Path = "D:\AI\work space\win\dist-nr\FluidDock.exe" }
)

Write-Output ("{0,-28} {1,9} {2,11} {3,11} {4,10}" -f "build", "on disk", "working set", "private", "cold start")

foreach ($b in $builds) {
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600

    $size = (Get-Item $b.Path).Length / 1MB

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $b.Path -PassThru
    $hwnd = 0
    while ($sw.ElapsedMilliseconds -lt 20000 -and $hwnd -eq 0) {
        try { $hwnd = [int](& "$PSScriptRoot\DockHwnd.ps1") } catch { }
    }
    $start = $sw.ElapsedMilliseconds

    # Let first-run JIT and icon rasterisation finish before reading memory, or the number is
    # measuring startup rather than what the dock costs while it sits there.
    Start-Sleep -Seconds 6
    $p.Refresh()

    Write-Output ("{0,-28} {1,6:N1} MB {2,8:N1} MB {3,8:N1} MB {4,7} ms" -f `
        $b.Name, $size, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $start)

    Stop-Process -Id $p.Id -Force
    Start-Sleep -Milliseconds 400
}
