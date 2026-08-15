# Answers one question: does dist\FluidDock.exe carry its own .NET, or borrow the machine's?
#
# The obvious test - point DOTNET_ROOT at a root with no 9.0 runtime - proves nothing, because
# the apphost consults the registry install location first and both builds survived it. So this
# compares loaded modules instead, with the dev build as the control: a framework-dependent app
# maps coreclr.dll from C:\Program Files\dotnet and that path shows up in the module list. A
# self-contained single-file app loads it out of the bundle, where it has no path on disk and
# never appears at all.
#
# Two builds, one measurement, and the control has to differ or the test means nothing.

function Probe($label, $path) {
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400

    $p = Start-Process $path -PassThru
    Start-Sleep -Seconds 3
    $p.Refresh()

    $fromDotnet = @($p.Modules | Where-Object { $_.FileName -like "*\dotnet\*" })
    Write-Output ("{0}" -f $label)
    Write-Output ("  modules total          {0}" -f $p.Modules.Count)
    Write-Output ("  loaded from a dotnet install  {0}" -f $fromDotnet.Count)
    foreach ($m in $fromDotnet | Select-Object -First 3) { Write-Output ("    {0}" -f $m.FileName) }
    Write-Output ""

    Stop-Process -Id $p.Id -Force
    Start-Sleep -Milliseconds 400
}

Probe "dev build (framework-dependent, the control)" "D:\AI\work space\win\src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe"
Probe "dist build (published)" "D:\AI\work space\win\dist\FluidDock.exe"
