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
    # NOT Start-Process. An SSH session is Windows session 0, which has no interactive desktop: ANGLE
    # cannot create a D3D11 swap chain there, so the shell comes up INVISIBLY and floods stderr with
    # SwapChain11 "Could not create additional swap chains" (HRESULT 0x887A0022) while every process
    # looks healthy. Hand the launch to the Task Scheduler with an Interactive principal instead, which
    # starts it in the logged-on user's session where there is a real desktop and GPU.
    $task = 'BevelInteractiveLaunch'
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

    # WindowsIdentity, NOT "$env:USERDOMAIN\$env:USERNAME": on a workgroup machine USERDOMAIN is
    # literally "WORKGROUP", which is a workgroup name rather than an account authority, so the
    # principal fails to resolve with "No mapping between account names and security IDs was done".
    # GetCurrent().Name gives the real authority (the machine name here, a domain elsewhere).
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $Dest
    $principal = New-ScheduledTaskPrincipal -UserId $identity `
                                            -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
    $procs = $null
    try {
        Start-ScheduledTask -TaskName $task

        # WAIT FOR THE PROCESS, don't sleep a fixed interval. Start-ScheduledTask returns as soon as the
        # request is queued, and unregistering the task out from under a launch that has not spawned yet
        # cancels it: a fixed 5s wait worked on a warm run and then failed on a cold one, reporting
        # "Launch did not produce any Bevel.App process" for a deploy that was otherwise fine.
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            $procs = Get-Process -Name 'Bevel.App' -ErrorAction SilentlyContinue
            if ($procs) { break }
            Start-Sleep -Milliseconds 250
        }
    } finally {
        # Only now: the task is a launch vehicle, not something to leave in the user's task list.
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }

    if (-not $procs) { throw "Launch did not produce any Bevel.App process within 45s." }

    # Let the launcher finish spawning core + taskbar so the session report covers the whole shell.
    Start-Sleep -Seconds 2
    $procs = Get-Process -Name 'Bevel.App' -ErrorAction SilentlyContinue

    # Report the session so an invisible session-0 launch can never be mistaken for a working one.
    $sessions = ($procs | Select-Object -Expand SessionId -Unique) -join ', '
    Write-Output "Launched $($procs.Count) process(es) in session(s) $sessions"
    if ($sessions -eq '0') {
        Write-Warning "Running in session 0 — there is no interactive desktop, so nothing will be visible."
    }
}
