param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BackupDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$statePath = Join-Path $runtime 'collector-state.json'
$staleTempPath = Join-Path $runtime 'collector-state.tmp'
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    throw "Missing collector state: $statePath"
}
if (-not (Test-Path -LiteralPath $staleTempPath -PathType Leaf)) {
    throw "Missing failed atomic-write temp file: $staleTempPath"
}

$blacklistReason = '5m:非计划EOB:2026-08-18T09:35:00'
$failureReason = '5m:非计划EOB:2026-08-18T09:40:00'

function Read-State([string]$path) {
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
}

function Matching-Keys([hashtable]$items, [string]$field, [string]$reason) {
    return @(
        $items.GetEnumerator() |
            Where-Object { [string]$_.Value[$field] -eq $reason } |
            ForEach-Object { [string]$_.Key }
    )
}

$state = Read-State $statePath
$stale = Read-State $staleTempPath
$stateBlacklistKeys = Matching-Keys $state.blacklist 'reason' $blacklistReason
$stateFailureKeys = Matching-Keys $state.failures 'lastError' $failureReason
$staleBlacklistKeys = Matching-Keys $stale.blacklist 'reason' $blacklistReason
$staleFailureKeys = Matching-Keys $stale.failures 'lastError' $failureReason

# These exact counts were established while the collector task was disabled.
# Refuse to touch the files if anything has changed or unrelated state exists.
if ($state.blacklist.Count -ne 1000 -or $stateBlacklistKeys.Count -ne 1000) {
    throw "Unexpected committed blacklist scope: total=$($state.blacklist.Count), matching=$($stateBlacklistKeys.Count)"
}
if ($state.failures.Count -ne 1694 -or $stateFailureKeys.Count -ne 1694) {
    throw "Unexpected committed failure scope: total=$($state.failures.Count), matching=$($stateFailureKeys.Count)"
}
if ($stale.blacklist.Count -ne 1000 -or $staleBlacklistKeys.Count -ne 1000) {
    throw "Unexpected stale-temp blacklist scope: total=$($stale.blacklist.Count), matching=$($staleBlacklistKeys.Count)"
}
if ($stale.failures.Count -ne 1695 -or $staleFailureKeys.Count -ne 1695) {
    throw "Unexpected stale-temp failure scope: total=$($stale.failures.Count), matching=$($staleFailureKeys.Count)"
}

New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
$backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path
$backupPath = Join-Path $backupRoot 'collector-state-boundary-pollution-20260818-095235.zip'
if (Test-Path -LiteralPath $backupPath) {
    throw "Backup already exists: $backupPath"
}
Compress-Archive -LiteralPath $statePath,$staleTempPath -DestinationPath $backupPath

foreach ($key in $stateBlacklistKeys) {
    $state.blacklist.Remove($key)
}
foreach ($key in $stateFailureKeys) {
    $state.failures.Remove($key)
}
if ($state.blacklist.Count -ne 0 -or $state.failures.Count -ne 0) {
    throw "Refusing to persist unexpected remaining state: blacklist=$($state.blacklist.Count), failures=$($state.failures.Count)"
}

$cleanupTempPath = Join-Path $runtime 'collector-state.cleanup.json'
$json = $state | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText(
    $cleanupTempPath,
    $json,
    [System.Text.UTF8Encoding]::new($false)
)
[System.IO.File]::Move($cleanupTempPath, $statePath, $true)
Remove-Item -LiteralPath $staleTempPath

$verified = Read-State $statePath
if ($verified.blacklist.Count -ne 0 -or $verified.failures.Count -ne 0) {
    throw 'Post-write verification failed.'
}
if (Test-Path -LiteralPath $staleTempPath) {
    throw 'Stale temp state still exists after cleanup.'
}

$backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
[pscustomobject]@{
    RemovedBlacklist = $stateBlacklistKeys.Count
    RemovedFailures = $stateFailureKeys.Count
    RemainingBlacklist = $verified.blacklist.Count
    RemainingFailures = $verified.failures.Count
    BackupPath = $backupPath
    BackupSha256 = $backupHash
}
