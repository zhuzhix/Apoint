[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'AStockMonitor-PairKlineCollector'
)

$ErrorActionPreference = 'Stop'
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Output "任务不存在：$TaskName"
    return
}

if ($PSCmdlet.ShouldProcess($TaskName, '停止并删除采集器开机任务')) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Output "已删除任务：$TaskName"
}
