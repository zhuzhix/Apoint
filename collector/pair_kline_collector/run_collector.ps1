[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonExe,
    [Parameter(Mandatory = $true)]
    [string]$MainPy,
    [Parameter(Mandatory = $true)]
    [string]$WaveWorkerPy,
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,
    [int]$RestartDelaySeconds = 60
)

$ErrorActionPreference = 'Continue'

# The Task Scheduler restart policy did not activate when the former direct
# python action disappeared with a stale successful result. Keep the task
# process resident and restart both collection roles explicitly.
while ($true) {
    $arguments = '"{0}" --config "{1}"' -f $MainPy, $ConfigPath
    $collector = Start-Process `
        -FilePath $PythonExe `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $MainPy) `
        -WindowStyle Hidden `
        -PassThru
    $waveArguments = '"{0}" --config "{1}"' -f $WaveWorkerPy, $ConfigPath
    $waveCollector = Start-Process `
        -FilePath $PythonExe `
        -ArgumentList $waveArguments `
        -WorkingDirectory (Split-Path -Parent $WaveWorkerPy) `
        -WindowStyle Hidden `
        -PassThru

    # The two roles form one deployment unit. A partial restart would leave
    # process ownership and health ambiguous, so either exit restarts both.
    while (-not $collector.HasExited -and -not $waveCollector.HasExited) {
        Start-Sleep -Seconds 1
        $collector.Refresh()
        $waveCollector.Refresh()
    }
    $failedRole = if ($collector.HasExited) { 'pair-kline' } else { 'wave-history' }
    $exitCode = if ($collector.HasExited) { $collector.ExitCode } else { $waveCollector.ExitCode }
    foreach ($process in @($collector, $waveCollector)) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit()
        }
    }

    # A forcibly terminated ProcessPoolExecutor parent can leave its six spawn
    # workers alive. Reusing their stale pipes is impossible and starting a new
    # pool beside them would violate the strict 1+6 process model. Reap only
    # direct children of the exited supervisor before starting its replacement.
    $rootProcessIds = @($collector.Id, $waveCollector.Id)
    $orphanWorkers = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $rootProcessIds -contains $_.ParentProcessId -and $_.Name -eq 'python.exe'
            }
    )
    foreach ($worker in $orphanWorkers) {
        Stop-Process -Id $worker.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Write-Error "AStockMonitor $failedRole collector exited with code $exitCode; reaped $($orphanWorkers.Count) child workers; restarting both roles in $RestartDelaySeconds seconds."
    Start-Sleep -Seconds $RestartDelaySeconds
}
