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
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    throw "Missing collector state: $statePath"
}

$reason = "1d:初始SDK分组在整个计划窗口没有任何有效计划行，按供应商/请求异常处理，禁止证明整组无成交 | 1d:EOB不完整 expected=1 actual=0 missing=['2026-08-18T15:00:00'] extra=[]"
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
$matching = @(
    $state.blacklist.GetEnumerator() |
        Where-Object { [string]$_.Value.reason -eq $reason } |
        ForEach-Object { [string]$_.Key }
)

# This exact scope was established after the scheduled task was disabled.
# Refuse to write if any unrelated or partially changed state is present.
if ($state.blacklist.Count -ne 4999 -or $matching.Count -ne 4999) {
    throw "Unexpected blacklist scope: total=$($state.blacklist.Count), matching=$($matching.Count)"
}
if ($state.failures.Count -ne 0) {
    throw "Unexpected failure state: $($state.failures.Count)"
}
if (Test-Path -LiteralPath (Join-Path $runtime 'collector-state.tmp')) {
    throw 'Unexpected collector-state.tmp exists; refusing cleanup.'
}

New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
$backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $backupRoot "collector-state-daily-bar-blacklist-$stamp.zip"
Compress-Archive -LiteralPath $statePath -DestinationPath $backupPath

foreach ($key in $matching) {
    $state.blacklist.Remove($key)
}
if ($state.blacklist.Count -ne 0 -or $state.failures.Count -ne 0) {
    throw 'Refusing to persist non-empty state after exact cleanup.'
}

$temporaryPath = Join-Path $runtime 'collector-state.cleanup.json'
$json = $state | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText(
    $temporaryPath,
    $json,
    [System.Text.UTF8Encoding]::new($false)
)
[System.IO.File]::Move($temporaryPath, $statePath, $true)

$verified = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
if ($verified.blacklist.Count -ne 0 -or $verified.failures.Count -ne 0) {
    throw 'Post-write verification failed.'
}

[pscustomobject]@{
    RemovedBlacklist = $matching.Count
    RemainingBlacklist = $verified.blacklist.Count
    RemainingFailures = $verified.failures.Count
    BackupPath = $backupPath
    BackupSha256 = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
}
