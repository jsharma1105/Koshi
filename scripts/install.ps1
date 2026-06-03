<#
.SYNOPSIS
    One-command installer for the Koshi MCP server (#74 Option A).

.DESCRIPTION
    Downloads the matching koshi-mcp AOT binary for the host RID from the
    latest GitHub release, verifies its SHA-256 against the release
    manifest, installs it under %LOCALAPPDATA%\Programs\Koshi, ensures the
    install directory is on the user PATH, then (unless -NoInit) delegates
    to `koshi-mcp init` for client / persona / team setup.

    Usage (web):
        irm https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.ps1 | iex

    Usage (local checkout):
        ./scripts/install.ps1 [flags]

.PARAMETER Prefix
    Install directory. Defaults to "$env:LOCALAPPDATA\Programs\Koshi".

.PARAMETER Version
    Release tag to install (e.g. "0.8.1" or "v0.8.1"). Defaults to latest.
    Can also be set via $env:KOSHI_VERSION.

.PARAMETER NoInit
    Skip the post-install `koshi-mcp init` wizard step. The binary is
    installed and PATH is updated, but no MCP client is wired up.

.PARAMETER NonInteractive
    Forwarded to `koshi-mcp init --non-interactive`. Auto-enabled when
    stdin is redirected (e.g. piped from `irm | iex`).

.PARAMETER Client
    Forwarded to `koshi-mcp init --client <name>`. Restricts wizard to a
    single MCP client (e.g. claude-desktop, copilot-cli).

.PARAMETER SkipIndex
    Forwarded to `koshi-mcp init --skip-index`.

.PARAMETER SkipPersonas
    Forwarded to `koshi-mcp init --skip-personas`.

.PARAMETER SkipTeam
    Forwarded to `koshi-mcp init --skip-team`.

.PARAMETER DryRun
    Print what would happen without mutating any state.

.PARAMETER NoMcpRegister
    Alias for -NoInit (kept for naming parity with install.sh).

.PARAMETER Help
    Show this help text and exit.

.NOTES
    Idempotent: re-runs replace the binary in place; PATH is not
    duplicated. The installer never modifies MCP client configs or
    persona files directly -- that work is delegated to `koshi-mcp init`.

    Security: the downloaded binary is verified against a SHA-256 hash
    fetched from the same GitHub release's manifest.json. This protects
    against transport corruption and accidental asset swap, but does NOT
    by itself guarantee authenticity if the GitHub release is compromised.
    Future versions will add signature / attestation verification.
#>
[CmdletBinding()]
param(
    [string]$Prefix,
    [string]$Version,
    [switch]$NoInit,
    [switch]$NoMcpRegister,
    [switch]$NonInteractive,
    [string]$Client,
    [switch]$SkipIndex,
    [switch]$SkipPersonas,
    [switch]$SkipTeam,
    [switch]$DryRun,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Script:Repo = 'jsharma1105/Koshi'
$Script:UserAgent = 'koshi-installer/1.0 (+https://github.com/jsharma1105/Koshi)'

function Write-Info($msg)    { Write-Host "[koshi] $msg" }
function Write-Step($msg)    { Write-Host "[koshi] $msg" -ForegroundColor Cyan }
function Write-OkLine($msg)  { Write-Host "[koshi] $msg" -ForegroundColor Green }
function Write-Warn2($msg)   { Write-Host "[koshi] $msg" -ForegroundColor Yellow }
function Write-Err2($msg)    { Write-Host "[koshi] $msg" -ForegroundColor Red }

function Show-Help {
    Get-Help $PSCommandPath -Detailed | Out-String | Write-Host
}

if ($Help) { Show-Help; exit 0 }

# ── 1. Detect RID ───────────────────────────────────────────────────────────
function Resolve-Rid {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ([string]::IsNullOrWhiteSpace($arch)) {
        $arch = (Get-CimInstance -ClassName Win32_Processor -ErrorAction SilentlyContinue).Architecture
    }
    switch -Regex ($arch) {
        '^(AMD64|x64|9)$' { return 'win-x64' }
        '^(ARM64|12)$'    { return 'win-arm64' }
        default {
            throw "Unsupported Windows architecture: '$arch'. Try 'dotnet tool install --global Koshi.Mcp' as an alternative."
        }
    }
}

# ── 2. Normalize version (strip leading 'v') ────────────────────────────────
function ConvertTo-NormalizedVersion([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return $v }
    $v = $v.Trim()
    if ($v.StartsWith('v') -or $v.StartsWith('V')) { return $v.Substring(1) }
    return $v
}

# ── 3. Resolve version ──────────────────────────────────────────────────────
function Resolve-Version([string]$explicit) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return (ConvertTo-NormalizedVersion $explicit) }
    if (-not [string]::IsNullOrWhiteSpace($env:KOSHI_VERSION)) { return (ConvertTo-NormalizedVersion $env:KOSHI_VERSION) }

    Write-Step "Resolving latest release tag from GitHub API…"
    try {
        $headers = @{ 'User-Agent' = $Script:UserAgent; 'Accept' = 'application/vnd.github+json' }
        $resp = Invoke-RestMethod -Uri "https://api.github.com/repos/$($Script:Repo)/releases/latest" -Headers $headers -TimeoutSec 30
        $tag = $resp.tag_name
        if ([string]::IsNullOrWhiteSpace($tag)) { throw "GitHub API returned an empty tag_name." }
        return (ConvertTo-NormalizedVersion $tag)
    } catch {
        throw "Failed to resolve latest version from GitHub API: $($_.Exception.Message). Set `$env:KOSHI_VERSION (e.g. '0.8.1') or pass -Version to skip the API call."
    }
}

# ── 4. Fetch + parse manifest ───────────────────────────────────────────────
function Get-Manifest([string]$version, [string]$rid) {
    $url = "https://github.com/$($Script:Repo)/releases/download/v$version/manifest.json"
    Write-Step "Fetching release manifest: $url"
    try {
        $headers = @{ 'User-Agent' = $Script:UserAgent }
        $manifest = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 30
    } catch {
        throw "Failed to download manifest.json for v$version : $($_.Exception.Message)"
    }
    if ($null -eq $manifest.binaries.$rid) {
        throw "Release v$version does not include a binary for RID '$rid'. Available: $($manifest.binaries.PSObject.Properties.Name -join ', ')"
    }
    return [pscustomobject]@{
        Version = $manifest.version
        Filename = $manifest.binaries.$rid.filename
        Sha256 = $manifest.binaries.$rid.sha256
        Url = "https://github.com/$($Script:Repo)/releases/download/v$version/$($manifest.binaries.$rid.filename)"
    }
}

# ── 5. PATH helpers ─────────────────────────────────────────────────────────
function Test-PathContainsDir([string]$pathValue, [string]$dir) {
    if ([string]::IsNullOrWhiteSpace($pathValue)) { return $false }
    $target = $dir.TrimEnd('\','/').ToLowerInvariant()
    foreach ($entry in $pathValue.Split(';')) {
        $e = $entry.Trim().Trim('"').TrimEnd('\','/').ToLowerInvariant()
        if ($e -eq $target) { return $true }
    }
    return $false
}

function Add-UserPath([string]$dir) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (Test-PathContainsDir $current $dir) {
        Write-Info "User PATH already contains $dir"
    } else {
        $new = if ([string]::IsNullOrWhiteSpace($current)) { $dir } else { "$current;$dir" }
        [Environment]::SetEnvironmentVariable('Path', $new, 'User')
        Write-OkLine "Added $dir to USER PATH (persisted via registry)"
    }
    if (-not (Test-PathContainsDir $env:Path $dir)) {
        $env:Path = "$dir;$env:Path"
        Write-Info "Updated current shell PATH"
    }
}

# ── 6. Download + verify ────────────────────────────────────────────────────
function Get-FileSha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

function Get-Binary([string]$url, [string]$expectedSha, [string]$dest) {
    # Use .new.exe (no embedded .exe.) so Windows can execute it for the
    # post-download smoke test. (PowerShell refuses to invoke files whose
    # extension is not in PATHEXT -- .tmp would fail with "Cannot run a
    # document in the middle of a pipeline".)
    $destDir = Split-Path -Parent $dest
    $destLeaf = [System.IO.Path]::GetFileNameWithoutExtension($dest)
    $tmp = Join-Path $destDir "$destLeaf.new.exe"
    Write-Step "Downloading $url"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    try {
        $headers = @{ 'User-Agent' = $Script:UserAgent }
        Invoke-WebRequest -Uri $url -Headers $headers -OutFile $tmp -UseBasicParsing -TimeoutSec 600
    } catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        throw "Failed to download binary from $url : $($_.Exception.Message)"
    }
    $actual = Get-FileSha256 $tmp
    $expected = $expectedSha.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch.`n  expected: $expected`n  actual:   $actual`n  URL: $url"
    }
    Write-OkLine "SHA-256 verified ($expected)"
    # Strip Mark-of-the-Web so Windows/AV doesn't block the freshly-downloaded
    # executable when we try to invoke it for the smoke test.
    try { Unblock-File -Path $tmp -ErrorAction Stop } catch { }
    return $tmp
}

function Test-TempBinary([string]$tmpPath) {
    Write-Step "Smoke-testing downloaded binary (--version)"
    # The SHA-256 verification above is the authoritative integrity check.
    # The smoke test is a defence-in-depth signal that the binary is actually
    # runnable on this host (wrong RID, missing OS feature, etc.). On Windows,
    # Defender / SmartScreen often blocks invocation of a freshly-downloaded
    # EXE for a variable window of seconds -- we retry with growing backoff,
    # but if we still can't invoke it we WARN rather than fail, because the
    # hash already proves the bits are correct and the user's MCP client will
    # surface any genuine execution problem on next start.
    $attempts = 5
    $lastErr = $null
    for ($i = 1; $i -le $attempts; $i++) {
        $stdoutFile = [System.IO.Path]::GetTempFileName()
        $stderrFile = [System.IO.Path]::GetTempFileName()
        try {
            $proc = Start-Process -FilePath $tmpPath -ArgumentList '--version' `
                -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdoutFile `
                -RedirectStandardError $stderrFile -ErrorAction Stop
            $stdout = Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue
            $stderr = Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue
            if (-not $stdout) { $stdout = '' }
            if (-not $stderr) { $stderr = '' }
            $combined = ($stdout + $stderr).Trim()
            if ($proc.ExitCode -ne 0) {
                throw "exit code $($proc.ExitCode): $combined"
            }
            Write-Info "Binary self-reports: $combined"
            return $true
        } catch {
            $lastErr = $_.Exception.Message
            if ($i -lt $attempts) {
                Start-Sleep -Milliseconds (1000 * [Math]::Pow(2, $i - 1))
            }
        } finally {
            Remove-Item $stdoutFile -ErrorAction SilentlyContinue
            Remove-Item $stderrFile -ErrorAction SilentlyContinue
        }
    }
    Write-Warn2 "Could not smoke-test downloaded binary after $attempts attempts: $lastErr"
    Write-Warn2 "This is usually a transient AV / SmartScreen scan on a fresh download."
    Write-Warn2 "SHA-256 was already verified against the published manifest -- proceeding."
    return $false
}

function Move-TempIntoPlace([string]$tmp, [string]$dest) {
    if (Test-Path $dest) {
        try {
            [System.IO.File]::Replace($tmp, $dest, $null)
            return
        } catch {
            try {
                Move-Item -Path $tmp -Destination $dest -Force
                return
            } catch {
                throw "Could not replace existing binary at $dest : $($_.Exception.Message)`nIf koshi-mcp is currently running (e.g. an MCP client is using it), stop it and re-run the installer."
            }
        }
    } else {
        Move-Item -Path $tmp -Destination $dest -Force
    }
}

# ── 7. Init delegation ──────────────────────────────────────────────────────
function Test-StdinRedirected {
    try { return [Console]::IsInputRedirected } catch { return $false }
}

function Test-WizardSupported([string]$binary) {
    # The `init` wizard subcommand was introduced after v0.8.1. Older
    # binaries silently fall through to launching the MCP stdio server,
    # which would hang the installer waiting for client messages on stdin.
    #
    # We probe in two layers, each with a strict timeout so a legacy binary
    # that ignores the argument and starts the stdio server can't hang the
    # installer:
    #
    #   1. `koshi-mcp init --help` -- exit 0 AND output mentions `init` is
    #      our authoritative signal. The exit code alone is not enough,
    #      because an older binary might scan all args for `--help`, print
    #      global help, and exit 0.
    #   2. Fall back to grepping `koshi-mcp --help` for `init` either at
    #      the start of a line OR alongside `koshi-mcp` (v0.9.0 lists it
    #      as `  koshi-mcp init   <description>` -- the first token is
    #      `koshi-mcp`, not `init`, which the older regex missed).
    $probeTimeoutMs = 5000

    function Invoke-HelpProbe([string]$bin, [string[]]$argv, [int]$timeoutMs) {
        $stdoutFile = [System.IO.Path]::GetTempFileName()
        $stderrFile = [System.IO.Path]::GetTempFileName()
        try {
            $proc = Start-Process -FilePath $bin -ArgumentList $argv `
                -NoNewWindow -PassThru `
                -RedirectStandardOutput $stdoutFile `
                -RedirectStandardError $stderrFile -ErrorAction Stop
            if (-not $proc.WaitForExit($timeoutMs)) {
                try { $proc.Kill() } catch { }
                return [pscustomobject]@{ Ok = $false; Out = ''; Reason = 'timeout' }
            }
            $out = Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue
            if (-not $out) { $out = '' }
            return [pscustomobject]@{ Ok = ($proc.ExitCode -eq 0); Out = $out; Reason = "exit=$($proc.ExitCode)" }
        } catch {
            return [pscustomobject]@{ Ok = $false; Out = ''; Reason = $_.Exception.Message }
        } finally {
            Remove-Item $stdoutFile -ErrorAction SilentlyContinue
            Remove-Item $stderrFile -ErrorAction SilentlyContinue
        }
    }

    $r = Invoke-HelpProbe $binary @('init','--help') $probeTimeoutMs
    if ($r.Ok -and $r.Out -match '(?im)\binit\b') { return $true }

    $r = Invoke-HelpProbe $binary @('--help') $probeTimeoutMs
    if ($r.Ok -and ($r.Out -match '(?im)^\s*init\b' -or $r.Out -match '(?im)\bkoshi-mcp\s+init\b')) { return $true }

    return $false
}

function Invoke-WizardInit([string]$binary) {
    if (-not (Test-WizardSupported $binary)) {
        Write-Warn2 "This binary does not yet support the 'init' wizard (likely v0.8.1 or older)."
        Write-Warn2 "Falling back to legacy setup. To wire up clients/personas, install"
        Write-Warn2 "the Koshi.Agents .NET tool (the shell installer ships koshi-mcp only):"
        Write-Warn2 "  dotnet tool install --global Koshi.Agents"
        Write-Warn2 "  koshi-agents install --client copilot   # or claude, cursor, windsurf"
        Write-Warn2 "When a release with the wizard ships, re-run this installer for the full flow."
        return 0
    }
    $initArgs = @('init')
    $autoNonInt = (Test-StdinRedirected)
    $effNonInt = $NonInteractive -or $autoNonInt
    if ($autoNonInt -and -not $NonInteractive) {
        Write-Warn2 "No interactive stdin detected; running `koshi-mcp init --non-interactive`."
        Write-Warn2 "Re-run `koshi-mcp init` from a terminal later for the full wizard."
    }
    if ($effNonInt)            { $initArgs += '--non-interactive' }
    if ($Client)               { $initArgs += @('--client', $Client) }
    if ($SkipIndex)            { $initArgs += '--skip-index' }
    if ($SkipPersonas)         { $initArgs += '--skip-personas' }
    if ($SkipTeam)             { $initArgs += '--skip-team' }
    Write-Step "Running: $binary $($initArgs -join ' ')"
    & $binary @initArgs
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    return $code
}

# ── Main ────────────────────────────────────────────────────────────────────
try {
    if (-not $Prefix) { $Prefix = Join-Path $env:LOCALAPPDATA 'Programs\Koshi' }
    $rid = Resolve-Rid
    $ver = Resolve-Version $Version
    $manifest = Get-Manifest -version $ver -rid $rid
    $dest = Join-Path $Prefix 'koshi-mcp.exe'

    Write-Host ""
    Write-Host "===== Koshi installer =====" -ForegroundColor Cyan
    Write-Host "  version  : $ver"
    Write-Host "  RID      : $rid"
    Write-Host "  binary   : $($manifest.Filename)"
    Write-Host "  source   : $($manifest.Url)"
    Write-Host "  sha256   : $($manifest.Sha256)"
    Write-Host "  prefix   : $Prefix"
    Write-Host "  dest     : $dest"
    Write-Host "  init     : $(if ($NoInit -or $NoMcpRegister) { 'skipped (--no-init)' } else { 'will run koshi-mcp init' })"
    Write-Host "  dry-run  : $($DryRun.IsPresent)"
    Write-Host ""

    if ($DryRun) {
        Write-Info "Dry-run mode: no files or registry entries will be modified."
        exit 0
    }

    if (-not (Test-Path $Prefix)) {
        New-Item -Path $Prefix -ItemType Directory -Force | Out-Null
    }

    $tmp = Get-Binary -url $manifest.Url -expectedSha $manifest.Sha256 -dest $dest
    $null = Test-TempBinary $tmp
    Move-TempIntoPlace -tmp $tmp -dest $dest
    Write-OkLine "Installed: $dest"

    Add-UserPath $Prefix

    if ($NoInit -or $NoMcpRegister) {
        Write-Info "Skipping wizard (--no-init)."
        Write-Host ""
        Write-Host "Next steps:" -ForegroundColor Cyan
        Write-Host "  1. Open a new terminal so the PATH update takes effect."
        Write-Host "  2. Run: koshi-mcp init"
        Write-Host "  3. Restart your MCP client(s) once."
        exit 0
    }

    $initCode = Invoke-WizardInit $dest

    Write-Host ""
    Write-Host "===== Next steps =====" -ForegroundColor Cyan
    Write-Host "  * Binary  : $dest"
    Write-Host "  * PATH    : $Prefix (new terminals will pick this up)"
    Write-Host "  * Re-run  : koshi-mcp init   (re-wire clients / personas / team)"
    Write-Host "  * Verify  : koshi-mcp --list-tools"
    Write-Host "  * Docs    : https://github.com/$($Script:Repo)#readme"
    Write-Host ""
    Write-Host "If you just registered a new client, restart it once to pick up the koshi MCP entry." -ForegroundColor Yellow
    exit $initCode
}
catch {
    Write-Err2 $_.Exception.Message
    exit 1
}
