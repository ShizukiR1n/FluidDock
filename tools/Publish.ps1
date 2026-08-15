# Builds the shippable exe into dist\.
#
# Self-contained on purpose. The dev build resolves .NET 9 through the registry to
# C:\Program Files\dotnet, which happens to be true on this machine and is not something a
# portable tool should depend on - the SDK next to it has 8.0 and 10.0 but no 9.0 at all, so the
# runtime being found is luck, not design. Self-contained carries its own.
#
# Not trimmed. Config loading is reflection-based System.Text.Json, the Composition surface goes
# through WinRT projections, and Vortice resolves D2D/D3D types dynamically; the trimmer cannot
# see any of that and would produce an exe that builds and then fails at runtime.
#
# Everything the dock reads at runtime - config\dock.json, assets\icons, fluiddock.log - is
# resolved from AppContext.BaseDirectory, which for a single-file publish is the folder holding
# the exe. So dist\ is self-describing: copy the folder, keep the settings.

# Compression and ReadyToRun are both off, and both measurements are in PackagingCost.ps1:
#
#   compressed      56.8 MB on disk   169.4 MB private   419 ms start
#   uncompressed   148.9 MB           55.3 MB            194 ms
#   no R2R          94.8 MB           46.7 MB            214 ms
#
# Compression is the trap. A single-file bundle is memory-mapped: the runtime pages assemblies
# straight out of the exe and shares them with every other .NET process. Compressed entries
# cannot be mapped, so they are inflated onto the private heap instead - 92 MB saved on a disk
# that has room, paid for with 114 MB of RAM in a process that sits there all day, plus double
# the startup. ReadyToRun buys nothing here either: the dock is a few thousand lines, so almost
# all of the startup is DWM and Composition bring-up rather than JIT, and the 20ms it might save
# is smaller than the run-to-run spread.
param(
    [string] $Dist = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist"),
    [switch] $Compress,
    [switch] $ReadyToRun
)

$ErrorActionPreference = "Stop"

$dotnet = "C:\Users\shayu\AppData\Local\Microsoft\dotnet\dotnet.exe"
$project = Join-Path (Split-Path $PSScriptRoot -Parent) "src\FluidDock\FluidDock.csproj"

Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# Wipe build output only. config\ and assets\ are the user's - a tuned dock.json and any hand-made
# icon overrides - and live in dist\ because that is where the exe looks for them. Reading them
# into a variable and writing them back afterwards is not good enough: this script deletes before
# it publishes, so any failure in between loses them for real. Ask me how I know.
if (Test-Path $Dist) {
    Get-ChildItem $Dist -Force |
        Where-Object { $_.Name -notin @("config", "assets") } |
        Remove-Item -Recurse -Force
}

# Distinct names: PowerShell variables are case-insensitive, so $compress would overwrite the
# $Compress switch itself and then fail to convert "false" back into one.
$compressFlag = if ($Compress) { "true" } else { "false" }
$r2rFlag = if ($ReadyToRun) { "true" } else { "false" }

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $Dist -v q --nologo `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=$compressFlag `
    /p:PublishReadyToRun=$r2rFlag `
    /p:DebugType=none `
    /p:SatelliteResourceLanguages=en

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $Dist "FluidDock.exe"

# Recursive, and config\ excluded. A flat -File listing of dist\ reported "none" while
# assets\tray.ico was missing from the package entirely - the one file the report existed to
# catch was in a subdirectory it never looked at.
$loose = @(Get-ChildItem $Dist -File -Recurse |
    Where-Object { $_.Name -ne "FluidDock.exe" -and $_.FullName -notlike "*\config\*" } |
    ForEach-Object { $_.FullName.Substring($Dist.Length).TrimStart('\') })

Write-Output ""
Write-Output ("exe    {0}" -f $exe)
Write-Output ("size   {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))
if ($loose.Count -eq 0) { Write-Output "loose  none" }
else { Write-Output ("loose  {0}" -f ($loose -join ", ")) }
