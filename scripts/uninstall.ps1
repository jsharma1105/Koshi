<#
.SYNOPSIS
    Removes the Koshi MCP server binary installed by install.ps1.

.DESCRIPTION
    Removes <prefix>\koshi-mcp.exe and removes <prefix> from the user PATH
    if it was added by install.ps1. By design this leaves the user's MCP
    client configuration, persona files, and indexed corpora intact so a
    later re-install does not lose state. Use -Purge for a clean wipe.

.PARAMETER Prefix
    Install directory. Defaults to "$env:LOCALAPPDATA\Programs\Koshi".

.PARAMETER Yes
    Skip the confirmation prompt.

.PARAMETER Help
    Show this help text and exit.
#>
[CmdletBinding()]
param(
    [string]$Prefix,
    [switch]$Yes,
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

if (-not $Yes) {
    $ans = Read-Host "Proceed with binary + PATH removal? [y/N]"
    if ($ans -notmatch '^[Yy]') { Write-Host "Aborted."; exit 0 }
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
