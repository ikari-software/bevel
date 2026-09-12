# Remote half of packaging/windows/deploy-ssh.sh (bevel-windeploy). Runs ON the Windows box.
#
# Kept as a real script rather than an inline `ssh … powershell -Command "…"` one-liner: the remote
# default shell here is PowerShell, so an inline command has to survive bash quoting AND PowerShell
# parsing, which silently mangles `$env:` expansions (it cost two round-trips to notice during setup).
# A file has exactly one layer of quoting.
param(
    [Parameter(Mandatory = $true)][string]$Archive,   # .tar.gz staged by the caller
    [Parameter(Mandatory = $true)][string]$Dest,      # install dir, e.g. C:\Users\ikari\Bevel
    [switch]$Launch                                   # start the shell after extracting
)

$ErrorActionPreference = 'Stop'

# Stop only OUR processes, by name, and report what was stopped. Never a blanket kill: this box is the
# user's, and a deploy script that guesses at process names can take out unrelated work.
$running = Get-Process -Name 'Bevel.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "Stopping $($running.Count) running Bevel.App process(es): $($running.Id -join ', ')"
    $running | Stop-Process -Force
    # Windows holds file locks briefly after exit; extracting over a locked DLL fails the whole deploy.
    Start-Sleep -Milliseconds 800
}

if (-not (Test-Path $Dest)) {
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
}

Write-Output "Extracting $Archive -> $Dest"
# bsdtar ships with Windows 10+/11; -m avoids clock-skew warnings when the archive was built on macOS.
tar -xzf $Archive -C $Dest -m
if ($LASTEXITCODE -ne 0) { throw "tar extraction failed with exit code $LASTEXITCODE" }

Remove-Item $Archive -Force -ErrorAction SilentlyContinue

$exe = Join-Path $Dest 'Bevel.App.exe'
if (-not (Test-Path $exe)) { throw "Deploy finished but $exe is missing — wrong archive layout?" }
Write-Output "Deployed: $exe"

if ($Launch) {
    Write-Output "Launching $exe"
    Start-Process -FilePath $exe -WorkingDirectory $Dest
}
