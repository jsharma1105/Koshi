<#
.SYNOPSIS
    Removes the Koshi MCP server binary installed by install.ps1.

.DESCRIPTION
    Removes <prefix>\koshi-mcp.exe and removes <prefix> from the user PATH
    if it was added by install.ps1. By design this leaves the user's MCP
    client configuration, persona files, and indexed corpora intact so a
    later re-install does not lose state.

    When stdin is not a TTY (e.g. piped through `irm | iex`), the
    confirmation prompt is auto-accepted.

    `dotnet tool install -g Koshi.*` installs are NOT removed by this
    script; they are detected and the matching `dotnet tool uninstall -g`
    commands are printed for you to run.

.PARAMETER Prefix
    Install directory. Defaults to "$env:LOCALAPPDATA\Programs\Koshi".

.PARAMETER Yes
    Skip the confirmation prompt.

.PARAMETER Force
    If a koshi-mcp.exe process is currently running FROM THIS PREFIX,
    stop it before removing files. Processes running from a different
    prefix (e.g. a side-by-side .NET-tool install) are NOT stopped --
    they are reported and the uninstall aborts.

.PARAMETER Help
    Show this help text and exit.
#>
[CmdletBinding()]
param(
    [string]$Prefix,
    [switch]$Yes,
    [switch]$Force,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Show-Help { Get-Help $PSCommandPath -Detailed | Out-String | Write-Host }
if ($Help) { Show-Help; exit 0 }

if (-not $Prefix) { $Prefix = Join-Path $env:LOCALAPPDATA 'Programs\Koshi' }
$dest = Join-Path $Prefix 'koshi-mcp.exe'

Write-Host ""
Write-Host "===== Koshi uninstaller =====" -ForegroundColor Cyan
Write-Host "  prefix : $Prefix"
Write-Host "  binary : $dest"
Write-Host ""
Write-Host "The following are NOT removed:" -ForegroundColor Yellow
Write-Host "  * MCP client config entries  (use your client's settings)"
Write-Host "  * Installed persona files     (use 'koshi-mcp uninstall-personas' if available)"
Write-Host "  * Indexed corpora / vaults    (under .koshi/ in your projects)"
Write-Host "  * Team registry               (.koshi/teams.json under each project)"
Write-Host ""

# Surface concurrent installs the user may not realise are present. The
# shell installer drops a binary at <prefix>, but `dotnet tool install -g
# Koshi.Mcp` writes to %USERPROFILE%\.dotnet\tools and wins on PATH for
# anyone who used the .NET path. Removing one without the other leaves the
# user thinking uninstall failed because `koshi-mcp` still resolves.
function Test-DotnetToolInstalled {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return @() }
    try {
        $list = & dotnet tool list -g 2>$null
    } catch { return @() }
    $found = @()
    foreach ($line in $list) {
        if ($line -match '^\s*koshi\.(mcp|agents)\b') { $found += $Matches[1] }
    }
    return $found
}

$dotnetTools = Test-DotnetToolInstalled
if ($dotnetTools.Count -gt 0) {
    Write-Host "Detected concurrent .NET tool install(s):" -ForegroundColor Yellow
    foreach ($t in $dotnetTools) {
        Write-Host "  * Koshi.$($t.Substring(0,1).ToUpper() + $t.Substring(1)) (installed via 'dotnet tool install -g')"
    }
    Write-Host "This script does NOT remove .NET-tool installs. To finish removal, run:" -ForegroundColor Yellow
    foreach ($t in $dotnetTools) {
        Write-Host "  dotnet tool uninstall -g Koshi.$($t.Substring(0,1).ToUpper() + $t.Substring(1))" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Auto-accept when stdin is redirected (irm | iex), since Read-Host would
# throw "PowerShell is in NonInteractive mode" and abort with a confusing
# error. The user opted in by piping the script to iex; honour it.
$stdinRedirected = $false
try { $stdinRedirected = [Console]::IsInputRedirected } catch { }

if (-not $Yes) {
    if ($stdinRedirected) {
        Write-Host "[koshi] stdin is not a TTY (running via 'irm | iex'); auto-accepting." -ForegroundColor Cyan
        $Yes = $true
    } else {
        $ans = Read-Host "Proceed with binary + PATH removal? [y/N]"
        if ($ans -notmatch '^[Yy]') { Write-Host "Aborted."; exit 0 }
    }
}

# Pre-flight: if koshi-mcp.exe is currently running (typically because an
# MCP client has it open as a subprocess), file replacement will fail with
# "Access denied" partway through. Detect early so the user gets a clear
# message instead of a corrupted partial state. We split running processes
# into "ours" (whose Path equals $dest after normalization -- safe to stop
# under -Force) and "others" (e.g. a side-by-side .NET-tool install -- we
# refuse to touch them even under -Force, since they aren't what this
# script is uninstalling).
function Resolve-NormalPath([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    try { return ([System.IO.Path]::GetFullPath($p)).TrimEnd('\','/').ToLowerInvariant() } catch { return $p.TrimEnd('\','/').ToLowerInvariant() }
}
$destNorm = Resolve-NormalPath $dest
$running = @(Get-Process -Name 'koshi-mcp' -ErrorAction SilentlyContinue)
$ours = @(); $others = @()
foreach ($p in $running) {
    try { $pp = Resolve-NormalPath $p.Path } catch { $pp = '' }
    if ($pp -eq $destNorm) { $ours += $p } else { $others += $p }
}
if ($others.Count -gt 0) {
    Write-Host "[koshi] koshi-mcp.exe is running from a different prefix (not this script's target):" -ForegroundColor Yellow
    foreach ($p in $others) { Write-Host "  PID $($p.Id)  $($p.Path)" }
    Write-Host "[koshi] This script will NOT stop those processes (they belong to a separate install)." -ForegroundColor Yellow
    Write-Host "[koshi] Close them yourself (or uninstall the other prefix) and re-run." -ForegroundColor Yellow
    exit 1
}
if ($ours.Count -gt 0) {
    Write-Host "[koshi] koshi-mcp.exe (this prefix) is currently running:" -ForegroundColor Yellow
    foreach ($p in $ours) { Write-Host "  PID $($p.Id)  $($p.Path)" }
    if ($Force) {
        foreach ($p in $ours) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                Write-Host "[koshi] Stopped PID $($p.Id)" -ForegroundColor Green
            } catch {
                Write-Host "[koshi] Could not stop PID $($p.Id): $($_.Exception.Message)" -ForegroundColor Red
                exit 1
            }
        }
        Start-Sleep -Seconds 1
    } else {
        Write-Host "[koshi] Close your MCP client(s) (Copilot CLI, Claude, etc.) and re-run," -ForegroundColor Yellow
        Write-Host "[koshi] or pass -Force to stop the listed processes automatically." -ForegroundColor Yellow
        exit 1
    }
}

if (Test-Path $dest) {
    try {
        Remove-Item $dest -Force
        Write-Host "[koshi] Removed $dest" -ForegroundColor Green
    } catch {
        Write-Host "[koshi] Failed to remove $dest : $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "[koshi] If koshi-mcp is currently running, stop it and re-run." -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host "[koshi] No binary at $dest (already removed?)" -ForegroundColor Yellow
}

# Remove prefix from USER PATH (exact entry match, case-insensitive).
$current = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not [string]::IsNullOrWhiteSpace($current)) {
    $target = $Prefix.TrimEnd('\','/').ToLowerInvariant()
    $keep = @()
    foreach ($entry in $current.Split(';')) {
        $e = $entry.Trim().Trim('"')
        $norm = $e.TrimEnd('\','/').ToLowerInvariant()
        if ($norm -ne $target -and -not [string]::IsNullOrWhiteSpace($e)) { $keep += $e }
    }
    $new = $keep -join ';'
    if ($new -ne $current) {
        [Environment]::SetEnvironmentVariable('Path', $new, 'User')
        Write-Host "[koshi] Removed $Prefix from USER PATH" -ForegroundColor Green
    } else {
        Write-Host "[koshi] $Prefix was not on USER PATH" -ForegroundColor Yellow
    }
}

# Remove the install dir if empty.
if (Test-Path $Prefix) {
    $remaining = Get-ChildItem -LiteralPath $Prefix -Force -ErrorAction SilentlyContinue
    if ($null -eq $remaining -or @($remaining).Count -eq 0) {
        Remove-Item -LiteralPath $Prefix -Force
        Write-Host "[koshi] Removed empty install dir $Prefix" -ForegroundColor Green
    } else {
        Write-Host "[koshi] Install dir $Prefix kept (not empty)." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "[koshi] Uninstall complete. Open a new terminal so the PATH change takes effect." -ForegroundColor Cyan
