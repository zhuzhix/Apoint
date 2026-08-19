[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonExe,
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'config.local.json'),
    [string]$TaskName = 'AStockMonitor-PairKlineCollector',
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'
$pythonPath = (Resolve-Path -LiteralPath $PythonExe).Path
$configFullPath = (Resolve-Path -LiteralPath $ConfigPath).Path
$scriptPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'main.py')).Path
$waveWorkerPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'wave_history_worker.py')).Path
$runnerPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'run_collector.ps1')).Path
$workingDirectory = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$interactiveUser = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).Name

if ($interactiveUser -eq 'NT AUTHORITY\SYSTEM') {
    throw 'The collector requires the desktop Goldminer terminal. Install the task for the interactive Goldminer user, not SYSTEM.'
}

if ([System.IO.Path]::GetExtension($pythonPath) -ne '.exe') {
    throw "PythonExe must be an absolute path to python.exe: $pythonPath"
}

# The scheduled task owns both Python entry points. Refuse registration if the
# new dedicated wave worker cannot even be imported by the selected runtime.
& $pythonPath -m py_compile $scriptPath $waveWorkerPath
if ($LASTEXITCODE -ne 0) {
    throw "Collector Python syntax validation failed; the scheduled task was not registered. Exit code: $LASTEXITCODE"
}
& $pythonPath $scriptPath --config $configFullPath --validate-config
if ($LASTEXITCODE -ne 0) {
    throw "Collector read-only configuration validation failed; the scheduled task was not registered. Exit code: $LASTEXITCODE"
}

$powershellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -PythonExe "{1}" -MainPy "{2}" -WaveWorkerPy "{3}" -ConfigPath "{4}"' -f `
    $runnerPath, $pythonPath, $scriptPath, $waveWorkerPath, $configFullPath
$action = New-ScheduledTaskAction `
    -Execute $powershellPath `
    -Argument $arguments `
    -WorkingDirectory $workingDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $interactiveUser
# The vendor terminal task starts 30 seconds after logon. Give its local SDK
# service another minute to become ready before starting the collector.
$trigger.Delay = 'PT90S'
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal `
    -UserId $interactiveUser `
    -LogonType Interactive `
    -RunLevel Highest

$task = New-ScheduledTask `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description 'AStockMonitor Python six-worker pair K-line collector plus one wave-history worker; requires the same interactive session as EastMoney Goldminer'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    # Task Scheduler can terminate the resident PowerShell wrapper without
    # terminating its Python supervisor/process-pool descendants. Snapshot the
    # exact collector tree before stopping the task, then reap only those PIDs.
    $processSnapshot = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $rootIds = @(
        $processSnapshot |
            Where-Object {
                $commandLine = [string]$_.CommandLine
                ($_.Name -eq 'powershell.exe' -and
                    $commandLine.IndexOf($runnerPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                ($_.Name -eq 'python.exe' -and
                    ($commandLine.IndexOf($scriptPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                     $commandLine.IndexOf($waveWorkerPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
                    $commandLine.IndexOf($configFullPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
            } |
            ForEach-Object { [int]$_.ProcessId }
    )
    $targetIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($rootId in $rootIds) {
        [void]$targetIds.Add($rootId)
    }
    do {
        $added = $false
        foreach ($process in $processSnapshot) {
            if ($targetIds.Contains([int]$process.ParentProcessId) -and
                $targetIds.Add([int]$process.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)

    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    foreach ($processId in $targetIds) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}
Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

if ($StartNow) {
    Start-ScheduledTask -TaskName $TaskName
}

Get-ScheduledTask -TaskName $TaskName |
    Select-Object TaskName, State, @{Name = 'Enabled'; Expression = { $_.Settings.Enabled } }
