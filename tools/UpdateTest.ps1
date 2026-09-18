# Does the in-app update actually update the app?
#
# A release is served from this machine rather than fetched from GitHub, so the test needs no
# token, no network and no real newer version: a tiny HTTP server answers "latest release" with
# a made-up tag (v9.9) whose one asset is whatever exe -Served names. The dock under test is
# started with FLUIDDOCK_RELEASES_API pointing at it, and everything from there on is the real
# code path - the JSON parse, the version compare, the redirect the asset URL answers with, the
# streamed download, the size and SHA-256 checks, the rename dance, the restart, the wait for
# the predecessor, and the clean-up of the exe it left behind.
#
# The proof is in what is on disk and who is running afterwards: the exe at -Exe must have the
# served file's size, the process must be a new one, and the .old the swap renamed aside must be
# gone - the new process deletes it, and it can only do that once the old one has really exited.
#
# Serving a different file than the one running makes the swap visible. The dev apphost is a
# few hundred KB and dist\FluidDock.exe is ~95 MB, so those are the defaults. The apphost is put
# back afterwards, so the build directory is left as it was found.
#
# Clicks are aimed at the update row by arithmetic from the bottom of the panel, the same way
# MenuTest aims at rows from the top: the row is a fixed distance above the last card, and the
# panel scrolled to its end puts the last card a fixed distance above the panel's bottom edge.
#
# Kept ASCII-only on purpose: Windows PowerShell 5.1 parses .ps1 as the system ANSI code page
# unless the file carries a UTF-8 BOM, so a Chinese literal here would be silently corrupted.
param(
    [string] $Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) "src\FluidDock\bin\Release\net9.0-windows10.0.19041.0\FluidDock.exe"),
    [string] $Served = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist\FluidDock.exe"),
    [int] $Port = 47123
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UT {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string w);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static IntPtr Menu() { return FindWindowW("FluidDockMenu", null); }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static bool Visible(IntPtr h) { return h != IntPtr.Zero && IsWindowVisible(h); }

    public static void Left()   { mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x0004,0,0,0,IntPtr.Zero); }
    public static void Wheel(int notches) { mouse_event(0x0800,0,0,unchecked((uint)(notches * 120)),IntPtr.Zero); }
    public static void Escape() { keybd_event(0x1B,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(0x1B,0,2,IntPtr.Zero); }
}
'@

# ---- Layout, restated (from MenuTheme; see MenuTest.ps1) -----------------------------------
$ShadowMargin  = 34
$FooterHeight  = 12
$PanelPadX     = 14
$PanelWidth    = 344
$RowHeight     = 44
$SectionHeader = 28
$SectionGap    = 14
$ScrollStrip   = 10          # added below the viewport when the content scrolls

$failures = 0
function Check($name, $condition, $detail) {
    $verdict = if ($condition) { "ok" } else { "FAIL" }
    [Console]::WriteLine(("  {0,-46} {1,-5} {2}" -f $name, $verdict, $detail))
    if (-not $condition) { $script:failures++ }
}

function Move-To($p) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($p.X, $p.Y)
    Start-Sleep -Milliseconds 120
}

function Click-At($p) { Move-To $p; [UT]::Left(); Start-Sleep -Milliseconds 250 }

if (-not (Test-Path $Exe)) { throw "no exe at $Exe - build first" }
if (-not (Test-Path $Served)) { throw "nothing to serve at $Served - run Publish.ps1 first" }

$exeDir = Split-Path $Exe
$backup = Join-Path $env:TEMP "FluidDock.apphost.bak"
$servedLength = (Get-Item $Served).Length
$sha = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($Served)
$digest = ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
$stream.Dispose()

$saved = [System.Windows.Forms.Cursor]::Position
$server = $null
$savedNoProxy = $null

try {
    # ---- the fake release ----------------------------------------------------------------
    $server = Start-Job -ArgumentList $Port, $Served, $servedLength, $digest -ScriptBlock {
        param($port, $served, $length, $digest)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$port/")
        $listener.Start()
        $json = '{"tag_name":"v9.9","assets":[{"name":"FluidDock.exe","url":"http://127.0.0.1:' + $port + '/asset","size":' + $length + ',"digest":"sha256:' + $digest + '"}]}'
        while ($true) {
            $ctx = $listener.GetContext()
            $path = $ctx.Request.Url.AbsolutePath
            try {
                if ($path -eq "/latest") {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
                    $ctx.Response.ContentType = "application/json"
                    $ctx.Response.ContentLength64 = $bytes.Length
                    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                }
                elseif ($path -eq "/asset") {
                    # What GitHub does for an authenticated asset request: a redirect elsewhere.
                    $ctx.Response.StatusCode = 302
                    $ctx.Response.RedirectLocation = "http://127.0.0.1:$port/file"
                }
                elseif ($path -eq "/file") {
                    $ctx.Response.ContentType = "application/octet-stream"
                    $ctx.Response.ContentLength64 = $length
                    $in = [System.IO.File]::OpenRead($served)
                    $in.CopyTo($ctx.Response.OutputStream)
                    $in.Dispose()
                }
                else { $ctx.Response.StatusCode = 404 }
            }
            finally { $ctx.Response.Close() }
        }
    }
    Start-Sleep -Milliseconds 800

    # This machine routes everything through a local proxy (HTTP_PROXY is set, and NO_PROXY does
    # not cover loopback), which answers 502 for a port it has never heard of. Neither the probe
    # nor the dock under test may go through it to reach the fake release.
    $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/latest" -UseBasicParsing -NoProxy
    Check "fake release answers" ($probe.StatusCode -eq 200) $probe.Content

    # ---- the dock under test --------------------------------------------------------------
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Copy-Item $Exe $backup -Force
    Remove-Item (Join-Path $exeDir "FluidDock.exe.old") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $exeDir "FluidDock.exe.new") -ErrorAction SilentlyContinue

    $env:FLUIDDOCK_RELEASES_API = "http://127.0.0.1:$Port/latest"
    $savedNoProxy = $env:NO_PROXY
    $env:NO_PROXY = if ($savedNoProxy) { "$savedNoProxy,127.0.0.1" } else { "127.0.0.1" }
    $old = Start-Process $Exe -WorkingDirectory $exeDir -PassThru
    Start-Sleep -Seconds 3
    Check "dock started" (-not $old.HasExited) "pid $($old.Id)"

    # ---- the panel, scrolled to its end ---------------------------------------------------
    & "$PSScriptRoot\TrayToggle.ps1" -Menu
    Start-Sleep -Milliseconds 700
    $menu = [UT]::Menu()
    Check "menu open" ([UT]::Visible($menu)) ("hwnd=0x{0:X}" -f [int64]$menu)
    if (-not [UT]::Visible($menu)) { throw "no panel; nothing further can be tested" }

    $rect = [UT]::Rect($menu)
    $panelL = $rect.L + $ShadowMargin
    $panelBottom = $rect.B - $ShadowMargin

    Move-To @{ X = $panelL + [int]($PanelWidth / 2); Y = $rect.T + $ShadowMargin + 160 }
    [UT]::Wheel(-60)
    Start-Sleep -Milliseconds 600

    # Counting up from the bottom edge: the scroll strip, the footer, the three rows of the last
    # card, its header, the gap, and then the update row - the only row in its card.
    $contentBottom = $panelBottom - $ScrollStrip - $FooterHeight
    $aboutTop = $contentBottom - 3 * $RowHeight - $SectionHeader
    $updateCenter = @{ X = $panelL + [int]($PanelWidth / 2); Y = $aboutTop - $SectionGap - [int]($RowHeight / 2) }

    # ---- check, then update ---------------------------------------------------------------
    Click-At $updateCenter                       # "check for updates"
    Start-Sleep -Milliseconds 1500
    Check "dock still running after check" (-not $old.HasExited) ""

    Click-At $updateCenter                       # "update to V9.9"

    $deadline = (Get-Date).AddSeconds(40)
    while (-not $old.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check "old process exited" $old.HasExited "pid $($old.Id)"

    $deadline = (Get-Date).AddSeconds(15)
    $fresh = $null
    while ((Get-Date) -lt $deadline) {
        $fresh = Get-Process FluidDock -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $old.Id } | Select-Object -First 1
        if ($fresh -and -not (Test-Path (Join-Path $exeDir "FluidDock.exe.old"))) { break }
        Start-Sleep -Milliseconds 250
    }
    Check "new process running" ($null -ne $fresh) $(if ($fresh) { "pid $($fresh.Id) from $($fresh.Path)" } else { "none" })
    Check "exe is the served file" ((Get-Item $Exe).Length -eq $servedLength) ("{0} bytes" -f (Get-Item $Exe).Length)
    Check "old exe cleaned up" (-not (Test-Path (Join-Path $exeDir "FluidDock.exe.old"))) ""
    Check "no leftover download" (-not (Test-Path (Join-Path $exeDir "FluidDock.exe.new"))) ""

    Start-Sleep -Seconds 2
    $dock = & "$PSScriptRoot\DockHwnd.ps1"
    Check "new dock has a window" ($dock -ne [IntPtr]::Zero) ("hwnd=0x{0:X}" -f [int64]$dock)
}
finally {
    [System.Windows.Forms.Cursor]::Position = $saved
    Get-Process FluidDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:\FLUIDDOCK_RELEASES_API -ErrorAction SilentlyContinue
    if ($null -ne $savedNoProxy) { $env:NO_PROXY = $savedNoProxy } else { Remove-Item Env:\NO_PROXY -ErrorAction SilentlyContinue }
    if (Test-Path $backup) {
        Copy-Item $backup $Exe -Force
        Remove-Item $backup -Force
    }
    Remove-Item (Join-Path $exeDir "FluidDock.exe.old") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $exeDir "FluidDock.exe.new") -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { throw "$failures check(s) failed" }
[Console]::WriteLine("PASS - the dock updated itself from the served release and came back.")
