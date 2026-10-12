<#
.SYNOPSIS
Records a Windows performance trace (WPR, built into Windows) of a flight, for tools/FlightTrace via
scripts/analyze-flight-spikes.py.

.DESCRIPTION
Run from an administrator PowerShell (Windows only lets an administrator record kernel events).

  scripts/record-flight-trace.ps1 start    # before launching DCS
  scripts/record-flight-trace.ps1 stop     # after leaving the mission; writes the trace (takes a minute)
  scripts/record-flight-trace.ps1 cancel   # drop a recording without writing it

Records the CPU profile: every processor sampled each millisecond with call stacks, and every context switch with
who woke the thread and from where. -Gpu adds the GPU profile (graphics kernel and video memory manager), for WPA.
Written to %LOCALAPPDATA%\DcsControl\traces\flight-<time>.etl; about 1-3 GB for a five-minute flight. Recording
costs some CPU of its own (stack walks on every context switch): a traced flight is for finding causes, not for an
A/B comparison against an untraced one.
#>
param(
    [Parameter(Position = 0)][ValidateSet('start', 'stop', 'cancel', 'status')][string]$Action = 'status',
    [switch]$Gpu
)
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($Action -ne 'status' -and -not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Recording needs an administrator PowerShell: right-click PowerShell, Run as administrator, and run this again.'
}

$traces = Join-Path $env:LOCALAPPDATA 'DcsControl\traces'
switch ($Action) {
    'start' {
        New-Item -ItemType Directory -Force $traces | Out-Null
        $profiles = @('-start', 'CPU')
        if ($Gpu) { $profiles += @('-start', 'GPU') }
        & wpr.exe @profiles -filemode -recordtempto $traces
        if ($LASTEXITCODE -ne 0) { throw "wpr -start failed ($LASTEXITCODE). If a recording is already running: scripts/record-flight-trace.ps1 cancel" }
        Write-Host "Recording. Fly, leave the mission, then: scripts/record-flight-trace.ps1 stop"
    }
    'stop' {
        $file = Join-Path $traces ("flight-{0:yyyyMMdd-HHmmss}.etl" -f (Get-Date))
        Write-Host 'Writing the trace (about a minute)...'
        & wpr.exe -stop $file 'DCS Control flight trace'
        if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed ($LASTEXITCODE)" }
        Write-Host "Written: $file"
        Write-Host "Analyse: python -I scripts/analyze-flight-spikes.py --etl `"$file`""
    }
    'cancel' { & wpr.exe -cancel }
    'status' { & wpr.exe -status }
}
