# Measures whether config hot-reload leaks. Launches the dock, samples private bytes and
# handles, drives N reloads by touching the config file, then samples again.
param([int]$Reloads = 20)

$dir    = "D:\AI\work space\win\src\FluidDock\bin\Release\net9.0-windows10.0.19041.0"
$exe    = Join-Path $dir "FluidDock.exe"
$config = Join-Path $dir "config\dock.json"

Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 500

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 5

function Sample($proc, $label) {
    $proc.Refresh()
    $mb = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
    "{0,-10} private {1,6} MB   handles {2,5}" -f $label, $mb, $proc.HandleCount
}

Sample $p "baseline"

for ($i = 1; $i -le $Reloads; $i++) {
    (Get-Item $config).LastWriteTime = Get-Date
    Start-Sleep -Milliseconds 500
    if ($i % 5 -eq 0) { Sample $p "after $i" }
}

Start-Sleep -Seconds 5
Sample $p "settled"

Stop-Process -Id $p.Id -Force -Confirm:$false
