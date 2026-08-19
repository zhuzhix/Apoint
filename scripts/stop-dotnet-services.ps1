$ErrorActionPreference = "Stop"
$names = @("AStockMonitor.Api")
foreach ($name in $names) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne "Stopped") {
        Stop-Service -Name $name -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}
Get-Service -Name $names -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType
