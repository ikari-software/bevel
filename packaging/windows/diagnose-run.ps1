# Instrumented Bevel run for BSOD forensics (bevel-winbsod). Runs ON the Windows box.
#
# The box bugchecked (0x3B SYSTEM_SERVICE_EXCEPTION) while Bevel was acting as its shell. A bugcheck
# stops the machine dead, so ANY evidence still sitting in a buffer is lost with it. Everything here is
# therefore written with an explicit flush-to-disk, or lives somewhere that survives independently:
#
#   durable by design   kernel dump (C:\Windows\MEMORY.DMP), WER user-mode dump of Bevel
#                       (C:\BevelCrash), and the Windows event logs — the kernel owns all three
#   flushed per line    this script's timeline + Bevel's own stdout/stderr
#   off-box             the caller's heartbeat log on the other machine, which is the only record
#                       that cannot be lost no matter how hard this one dies
#
# Deliberately NOT used: Driver Verifier. It would give a far sharper answer on a driver fault, but a
# misconfigured Verifier can leave a machine unbootable, and that is not a risk to take on someone
# else's desktop without asking first.
param(
    [string]$Exe = 'C:\Users\ikari\Bevel\Bevel.App.exe',
    [string]$LogDir = 'C:\BevelCrash',
    [int]$SampleMs = 500,
    [switch]$SoftwareRender   # force CPU rendering, to test whether the GPU driver is implicated
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$timeline  = Join-Path $LogDir "timeline-$stamp.log"
$appOut    = Join-Path $LogDir "bevel-stdout-$stamp.log"
$appErr    = Join-Path $LogDir "bevel-stderr-$stamp.log"

# StreamWriter with AutoFlush still only reaches the OS cache; Flush($true) forces the filesystem to
# commit, which is the difference between having the last second before a bugcheck and not.
$fs = [IO.File]::Open($timeline, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$w  = New-Object IO.StreamWriter($fs)
function Note([string]$msg) {
    $w.WriteLine(("[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $msg))
    $w.Flush(); $fs.Flush($true)
}

Note "=== instrumented run $stamp ==="
Note ("exe=$Exe softwareRender=$SoftwareRender")
Note ("os=" + [Environment]::OSVersion.VersionString + " boot=" + (Get-CimInstance Win32_OperatingSystem).LastBootUpTime)
foreach ($g in Get-CimInstance Win32_VideoController) {
    Note ("gpu=" + $g.Name + " driver=" + $g.DriverVersion + " date=" + $g.DriverDate + " status=" + $g.Status)
}
Note ("session=" + (qwinsta 2>&1 | Out-String).Trim().Replace("`r`n", " | "))

# Launch into the INTERACTIVE session (see deploy-remote.ps1): a process started from an SSH session
# lands in session 0, where there is no desktop and the GPU path cannot even initialise.
$task = 'BevelDiagnoseLaunch'
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

# A .cmd FILE, not `cmd /c "..." > "..."`. A scheduled-task action cannot redirect by itself, and
# Bevel's ANGLE/D3D complaints go to stderr — the loudest signal last time — so redirection is
# required. But cmd's quote parsing mangles `/c "exe" > "log"` (it exited 1 and wrote nothing), so the
# command goes in a file and the task just runs the file: one layer of quoting instead of three. Same
# reason the remote commands travel as -EncodedCommand.
$runner = Join-Path $LogDir "run-bevel-$stamp.cmd"
$lines = @('@echo off')
if ($SoftwareRender) { $lines += 'set AVALONIA_RENDERING_MODE=Software' }
$lines += ('"{0}" > "{1}" 2> "{2}"' -f $Exe, $appOut, $appErr)
Set-Content -Path $runner -Value $lines -Encoding ASCII
Note ("runner=$runner")
$action = New-ScheduledTaskAction -Execute $runner -WorkingDirectory (Split-Path $Exe)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null

Note "starting scheduled task (interactive session)"
Start-ScheduledTask -TaskName $task

# Wait for the process rather than sleeping a fixed interval — unregistering the task out from under a
# launch that has not spawned yet cancels it.
$deadline = (Get-Date).AddSeconds(45)
$procs = $null
while ((Get-Date) -lt $deadline) {
    $procs = Get-Process -Name 'Bevel.App' -ErrorAction SilentlyContinue
    if ($procs) { break }
    Start-Sleep -Milliseconds 250
}
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

if (-not $procs) { Note "FAILED: no Bevel.App process appeared within 45s"; $w.Close(); $fs.Close(); exit 1 }
Note ("launched pids=" + (($procs | Select-Object -Expand Id) -join ',') +
      " sessions=" + (($procs | Select-Object -Expand SessionId -Unique) -join ','))

# Sample until the caller stops us — or until the machine does.
$lastEvent = Get-Date
while ($true) {
    $p = Get-Process -Name 'Bevel.App' -ErrorAction SilentlyContinue
    if (-not $p) { Note "ALL BEVEL PROCESSES GONE (clean exit or user-mode crash)"; break }

    $sum = ($p | ForEach-Object {
        "{0}:ws={1}MB,hnd={2},thr={3}" -f $_.Id, [math]::Round($_.WorkingSet64/1MB), $_.HandleCount, $_.Threads.Count
    }) -join ' '
    Note ("procs " + $sum)

    # Surface driver/GPU events as they land, so the timeline shows what the system saw just before a
    # bugcheck — a TDR (4101) immediately before would implicate the display driver directly.
    $evts = Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$lastEvent} -MaxEvents 20 -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -in 4101,4100,14,13,1001,41,6008 -or $_.ProviderName -match 'Display|amd|Video|Kernel-Power' }
    foreach ($e in $evts) { Note ("EVENT " + $e.ProviderName + " id=" + $e.Id + " :: " + (($e.Message -split "`n")[0])) }
    $lastEvent = Get-Date

    Start-Sleep -Milliseconds $SampleMs
}

Note "=== watcher exiting ==="
$w.Close(); $fs.Close()
