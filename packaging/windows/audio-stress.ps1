# Isolation test for the 0x3B bugcheck (bevel-winbsod). Runs ON the Windows box, in the INTERACTIVE
# session — audio endpoint activation behaves differently from session 0.
#
# WHAT THIS IS TESTING. The crash stack was entirely audio and contained no Bevel frame:
#   svchost -> ksthunk -> AtihdWT6.sys (AMD HDMI audio) -> portcls -> ks!KsPinDataIntersection
#   -> portcls!PinIntersectHandler -> portcls!ValidateTypeAndSpecifier  (access violation)
# That is audio-endpoint FORMAT NEGOTIATION. The box has an active HDMI endpoint, "1 - DENON-AVR",
# and an AV receiver advertises a long, exotic format list (multichannel PCM, bitstream) — far more
# than built-in speakers. Bevel reaches this path through WindowsAudioPlayback -> winmm PlaySound for
# its theme sounds.
#
# So: does a BARE PlaySound, with no Bevel running at all, take the machine down? If yes, the defect is
# in the driver and Bevel is merely the first thing on this box to touch it — a user-mode PlaySound must
# never be able to bugcheck Windows. If no, the trigger is something more specific to Bevel and the next
# round narrows from there. Either answer is worth more than another undirected Bevel run.
param(
    [string]$LogDir = 'C:\BevelCrash',
    [int]$Iterations = 200,
    [int]$GapMs = 250
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$log = Join-Path $LogDir ("audio-stress-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

$fs = [IO.File]::Open($log, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$w  = New-Object IO.StreamWriter($fs)
function Note([string]$m) {
    $w.WriteLine(("[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m))
    $w.Flush(); $fs.Flush($true)   # to DISK: an OS-cache write dies with the bugcheck
}

Add-Type -Name W -Namespace Mm -MemberDefinition @'
[DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);
'@

# The exact flags Bevel uses (WindowsAudioPlayback.Play).
$SND_ASYNC = 0x0001; $SND_NODEFAULT = 0x0002; $SND_FILENAME = 0x00020000

$wav = 'C:\Windows\Media\Windows Ding.wav'
if (-not (Test-Path $wav)) { $wav = (Get-ChildItem 'C:\Windows\Media' -Filter *.wav | Select-Object -First 1).FullName }

Note "=== audio isolation test: winmm PlaySound, NO Bevel running ==="
Note ("wav=$wav iterations=$Iterations gapMs=$GapMs")
Note ("bevel processes present = " + ((Get-Process -Name Bevel.App -EA SilentlyContinue | Measure-Object).Count))
foreach ($d in Get-CimInstance Win32_SoundDevice) { Note ("device=" + $d.Name + " status=" + $d.Status) }

for ($i = 1; $i -le $Iterations; $i++) {
    # Alternate a real play with an explicit stop (null): stopping forces the endpoint to be released
    # and re-acquired, which is what makes Windows renegotiate formats — the crashing path — rather
    # than reusing an already-open stream.
    Note ("play #$i")
    [Mm.W]::PlaySound($wav, [IntPtr]::Zero, $SND_FILENAME -bor $SND_ASYNC -bor $SND_NODEFAULT) | Out-Null
    Start-Sleep -Milliseconds $GapMs
    Note ("stop #$i")
    [Mm.W]::PlaySound($null, [IntPtr]::Zero, 0) | Out-Null
    Start-Sleep -Milliseconds $GapMs
}

Note "=== survived all iterations ==="
$w.Close(); $fs.Close()
