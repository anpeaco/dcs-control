<#
.SYNOPSIS
  External A/B measurement of the DCS engine optimizations, for setups where the
  module's own suite cannot measure: any non-quad-view headset, or the monitor.

.DESCRIPTION
  The in-module benchmark is driven by a frame hook that only fires on quad-view
  frames (main.cpp's Classify needs at least 4 view collections forming
  focus/peripheral pairs), so on a stereo headset the suite reports "no
  quad-view frames measured". This measures the same thing from outside:

    1. captures per-frame times of DCS.exe with Intel PresentMon;
    2. alternates the optimizations OFF/ON in equal blocks by dropping
       toggle_engine.flag next to the module (the module polls for it once a
       second and logs "hotkey: engine optimizations OFF|ON");
    3. writes a schedule sidecar and hands both to analyze-engine-ab.py, which
       pairs each ON block against its neighbouring OFF blocks and reports the
       change with a Student's t 95% interval, the method bench.h uses.

  Block boundaries come from this script's own stopwatch, started with the
  trace, so no wall-clock correlation is needed. The module's <= 1 s flag poll
  is absorbed by -SettleSeconds, discarded at the start of every block. The
  module log is read only to confirm the toggles landed.

  Nothing here writes to DCS or changes the module's settings.

.EXAMPLE
  ./scripts/measure-engine-ab.ps1 -PresentMon C:\tools\PresentMon.exe
#>
[CmdletBinding()]
param(
    # PresentMon executable, from github.com/GameTechDev/PresentMon/releases.
    # Either the 1.x or the 2.x CLI; the argument style is detected.
    [string]$PresentMon,
    # Seconds per block. Forced even so the module ends in its starting state.
    [int]$BlockSeconds = 30,
    [int]$Blocks = 8,
    # Discarded at the start of each block: covers the 1 s flag poll plus
    # frame-time settling after the optimizations change.
    [double]$SettleSeconds = 4,
    [string]$ModuleDir,
    [string]$OutDir,
    # Overrides the detected PresentMon arguments entirely. Use {pid}, {csv}
    # and {seconds} as placeholders.
    [string[]]$PresentMonArgs,
    # Run every check and print the plan without capturing or toggling
    # anything. Use this to validate a setup against a live flight safely.
    [switch]$DryRun,
    # Skip the confirmation prompt (for unattended runs).
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------ module dir
if (-not $ModuleDir) {
    $bases = @()
    try {
        $shell = New-Object -ComObject Shell.Application
        $self = $shell.Namespace('shell:SavedGames')
        if ($self) { $bases += $self.Self.Path }
    } catch { }
    $bases += (Join-Path $env:USERPROFILE 'Saved Games')
    foreach ($base in $bases) {
        foreach ($variant in @('DCS', 'DCS.openbeta')) {
            $path = Join-Path $base "$variant\Scripts\DcsQvCull"
            if ((-not $ModuleDir) -and (Test-Path -LiteralPath $path)) { $ModuleDir = $path }
        }
    }
}
if ((-not $ModuleDir) -or (-not (Test-Path -LiteralPath $ModuleDir))) {
    Fail 'Could not find Saved Games\DCS\Scripts\DcsQvCull. Apply a profile with Engine Optimizations once, or pass -ModuleDir.'
}
$ModuleDir = (Resolve-Path -LiteralPath $ModuleDir).Path
$logPath = Join-Path $ModuleDir 'DcsQvCull.log'
$flagPath = Join-Path $ModuleDir 'toggle_engine.flag'

# ------------------------------------------------------------------- preflight
$dcs = Get-Process -Name 'DCS' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dcs) { Fail 'DCS.exe is not running. Start DCS, get into a flight, then run this again.' }

if (-not (Test-Path -LiteralPath $logPath)) {
    Fail "No DcsQvCull.log in ${ModuleDir}: the module has not loaded. Check that Engine Optimizations is enabled and that DCS started after the profile was applied."
}
$logStartLength = (Get-Item -LiteralPath $logPath).Length

if (-not $PresentMon) {
    $found = Get-Command -Name 'PresentMon.exe' -ErrorAction SilentlyContinue
    if ($found) { $PresentMon = $found.Source }
}
if ((-not $PresentMon) -or (-not (Test-Path -LiteralPath $PresentMon))) {
    Fail 'PresentMon not found. Download it from https://github.com/GameTechDev/PresentMon/releases and pass -PresentMon <path to the exe>.'
}
$PresentMon = (Resolve-Path -LiteralPath $PresentMon).Path
$PresentMonName = Split-Path -Leaf $PresentMon

if ($Blocks -lt 4) { $Blocks = 4 }
$Blocks = $Blocks - ($Blocks % 2)
$halfBlock = [Math]::Floor($BlockSeconds / 2)
if ($SettleSeconds -ge $halfBlock) { $SettleSeconds = $halfBlock }

if (-not $OutDir) { $OutDir = Join-Path $ModuleDir ('ab-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
# The directory is created once the run is confirmed, so -DryRun and a
# declined prompt leave no trace behind.
$csvPath = Join-Path $OutDir 'frames.csv'
$schedulePath = Join-Path $OutDir 'schedule.json'
$reportPath = Join-Path $OutDir 'report.txt'

# The module starts in whatever state the profile applied; the newest toggle
# line in the log wins, so an interrupted earlier run is accounted for.
$startState = 'on'
$toggleLines = Select-String -LiteralPath $logPath -Pattern 'engine optimizations (OFF|ON)' -AllMatches
if ($toggleLines) {
    $last = $toggleLines | Select-Object -Last 1
    $lastMatch = $last.Matches[$last.Matches.Count - 1]
    if ($lastMatch.Groups[1].Value -eq 'OFF') { $startState = 'off' }
}

$totalSeconds = ($Blocks * $BlockSeconds) + 10
Write-Host "Module:      $ModuleDir"
Write-Host "DCS:         PID $($dcs.Id)"
Write-Host "PresentMon:  $PresentMon"
Write-Host "Start state: optimizations $($startState.ToUpper()) (from the module log)"
Write-Host ("Plan:        {0} blocks x {1}s, {2}s discarded each, {3:n1} min total" -f $Blocks, $BlockSeconds, $SettleSeconds, ($Blocks * $BlockSeconds / 60.0))
Write-Host ''
Write-Host 'This toggles the engine optimizations in your RUNNING DCS session'  -ForegroundColor Yellow
Write-Host ("{0} times, then restores the starting state." -f $Blocks)          -ForegroundColor Yellow
Write-Host 'Park in a busy, CPU-bound scene and keep the view completely still' -ForegroundColor Yellow
Write-Host 'until it finishes. Any change in the scene invalidates the result.'  -ForegroundColor Yellow
Write-Host ''

if ($DryRun) {
    Write-Host 'Dry run: every check above passed. Nothing was captured or toggled.' -ForegroundColor Green
    Write-Host 'Re-run without -DryRun to measure.'
    exit 0
}
if (-not $Yes) {
    $answer = $null
    try { $answer = Read-Host 'Start the measurement? [y/N]' }
    catch {
        # Non-interactive host: refuse rather than start toggling a live flight
        # that nobody confirmed.
        Fail 'This shell is non-interactive, so the confirmation could not be asked. Re-run with -Yes to confirm up front, or -DryRun to check the setup. Nothing was changed.'
    }
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host 'Cancelled; nothing was changed.'
        exit 0
    }
    Write-Host ''
}

# --------------------------------------------------------------------- capture
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Remove-Item -LiteralPath $flagPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $csvPath -Force -ErrorAction SilentlyContinue

# PresentMon 1.x takes single-dash underscore switches, 2.x double-dash hyphen
# ones. --timed/-timed makes it stop and flush on its own, which is the only
# clean scripted stop. Try each style and keep the first that stays alive.
if ($PresentMonArgs) {
    $styles = @(, $PresentMonArgs)
} else {
    $styles = @(
        @('-process_id', '{pid}', '-output_file', '{csv}', '-timed', '{seconds}', '-stop_existing_session', '-no_top', '-terminate_on_proc_exit'),
        @('--process-id', '{pid}', '--output-file', '{csv}', '--timed', '{seconds}', '--stop-existing-session', '--terminate-on-proc-exit'),
        @('--process_id', '{pid}', '--output_file', '{csv}', '--timed', '{seconds}', '--stop_existing_session', '--no_top')
    )
}

$pm = $null
foreach ($style in $styles) {
    $pmArgs = @()
    foreach ($token in $style) {
        $pmArgs += $token.Replace('{pid}', [string]$dcs.Id).Replace('{csv}', $csvPath).Replace('{seconds}', [string]$totalSeconds)
    }
    Remove-Item -LiteralPath $csvPath -Force -ErrorAction SilentlyContinue
    $candidate = Start-Process -FilePath $PresentMon -ArgumentList $pmArgs -PassThru -WindowStyle Hidden

    # Staying alive is not proof it understood the arguments -- any program that
    # waits would pass that. Require a CSV that actually grows: that is the only
    # evidence it is capturing. Without this the run burns minutes and collects
    # nothing.
    $capturing = $false
    $deadline = (Get-Date).AddSeconds(10)
    $firstSize = -1
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ($candidate.HasExited) { break }
        if (Test-Path -LiteralPath $csvPath) {
            $size = (Get-Item -LiteralPath $csvPath).Length
            if ($firstSize -lt 0) { $firstSize = $size }
            elseif ($size -gt $firstSize -and $size -gt 0) { $capturing = $true; break }
        }
    }
    if ($capturing) { $pm = $candidate; break }

    if ($candidate.HasExited) {
        Write-Host "  ($PresentMonName rejected the '$($style[0])' style, exit $($candidate.ExitCode); trying another)"
    } else {
        Write-Host "  (no growing CSV with the '$($style[0])' style; stopping it and trying another)"
        try { Stop-Process -Id $candidate.Id -Force -ErrorAction Stop } catch { }
        $candidate.WaitForExit(5000) | Out-Null
    }
}
if (-not $pm) {
    Fail @"
PresentMon never started capturing (no CSV grew) with any argument style.

Likely causes, in order:
  1. Not elevated: PresentMon needs administrator rights for ETW tracing.
     Run this script from an administrator PowerShell.
  2. The exe passed to -PresentMon is not PresentMon.
  3. A newer PresentMon with different switches: pass -PresentMonArgs
     yourself, using {pid}, {csv} and {seconds} as placeholders, e.g.
       -PresentMonArgs '--process-id','{pid}','--output-file','{csv}','--timed','{seconds}'

Nothing was changed; your optimizations are untouched.
"@
}
$clock = [Diagnostics.Stopwatch]::StartNew()

$schedule = New-Object System.Collections.ArrayList
$state = $startState
try {
    for ($i = 0; $i -lt $Blocks; $i++) {
        $blockStart = $clock.Elapsed.TotalSeconds
        $target = $blockStart + $BlockSeconds
        Write-Host ("  block {0,2}/{1}  optimizations {2,-3}" -f ($i + 1), $Blocks, $state.ToUpper()) -NoNewline
        while ($clock.Elapsed.TotalSeconds -lt $target) {
            Start-Sleep -Milliseconds 250
            if ($pm.HasExited) { throw 'PresentMon stopped early (did DCS exit?).' }
        }
        $blockEnd = $clock.Elapsed.TotalSeconds
        [void]$schedule.Add([ordered]@{
            index = $i + 1
            state = $state
            start = [Math]::Round($blockStart, 3)
            end   = [Math]::Round($blockEnd, 3)
        })
        # Ask the module to flip. It picks the flag up within a second; the next
        # block discards SettleSeconds to cover that.
        New-Item -ItemType File -Path $flagPath -Force | Out-Null
        if ($state -eq 'on') { $state = 'off' } else { $state = 'on' }
        Write-Host '  done'
    }
}
finally {
    # --timed stops PresentMon itself; wait for that so the CSV is flushed.
    if (-not $pm.HasExited) {
        Write-Host 'Waiting for PresentMon to flush...'
        if (-not $pm.WaitForExit(20000)) {
            try { Stop-Process -Id $pm.Id -Force -ErrorAction Stop } catch { }
            $pm.WaitForExit(5000) | Out-Null
        }
    }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $flagPath -Force -ErrorAction SilentlyContinue
}

if ($state -ne $startState) {
    Write-Host 'Restoring the starting state...'
    New-Item -ItemType File -Path $flagPath -Force | Out-Null
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $flagPath -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------- verify
$observed = @()
$stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
try {
    $stream.Seek($logStartLength, 'Begin') | Out-Null
    $reader = New-Object IO.StreamReader($stream)
    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine()
        if ($line -match 'engine optimizations (OFF|ON)') { $observed += $Matches[1] }
    }
} finally { $stream.Dispose() }

Write-Host ("Toggles:     {0} logged by the module, {1} requested" -f $observed.Count, $Blocks)
if ($observed.Count -lt $Blocks) {
    Write-Host 'WARNING: the module logged fewer toggles than requested, so some blocks may be mislabelled. Treat the result with suspicion.' -ForegroundColor Yellow
}

if (-not (Test-Path -LiteralPath $csvPath)) { Fail "PresentMon wrote no CSV to $csvPath." }

$payload = [ordered]@{
    blockSeconds  = $BlockSeconds
    settleSeconds = $SettleSeconds
    startState    = $startState
    loggedToggles = $observed
    blocks        = $schedule
}
$payload | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $schedulePath -Encoding utf8

# --------------------------------------------------------------------- analyse
if (Get-Command -Name 'py' -ErrorAction SilentlyContinue) { $pyExe = 'py' } else { $pyExe = 'python' }
$analyzer = Join-Path $PSScriptRoot 'analyze-engine-ab.py'
Write-Host ''
& $pyExe $analyzer $csvPath $schedulePath --report $reportPath
Write-Host ''
Write-Host "Raw frames:  $csvPath"
Write-Host "Report:      $reportPath"
