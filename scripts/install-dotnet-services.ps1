param(
    [string]$PublishRoot = "",
    [switch]$Start
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $Root "deploy\services"
}
$PublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
if (-not $PublishRoot.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PublishRoot must remain inside the project directory: $Root"
}

$services = @(
    @{ Name = "AStockMonitor.Api"; Project = "src\AStockMonitor.Api\AStockMonitor.Api.csproj"; Exe = "AStockMonitor.Api.exe" }
)

# Stop every service before publishing into shared deployment directories. Each
# process loads shared assemblies from its own folder and Windows locks those DLLs.
foreach ($service in $services) {
    $existing = Get-Service -Name $service.Name -ErrorAction SilentlyContinue
    if ($null -ne $existing -and $existing.Status -ne "Stopped") {
        Stop-Service -Name $service.Name -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}

foreach ($service in $services) {
    $output = Join-Path $PublishRoot $service.Name
    dotnet publish (Join-Path $Root $service.Project) -c Release --no-restore -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed: $($service.Name)"
    }

    $binary = Join-Path $output $service.Exe
    if (-not (Test-Path -LiteralPath $binary)) {
        throw "Published service executable was not found: $binary"
    }
    $existing = Get-Service -Name $service.Name -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        & sc.exe create $service.Name binPath= ('"' + $binary + '"') start= delayed-auto
        if ($LASTEXITCODE -ne 0) { throw "Service create failed: $($service.Name)" }
    } else {
        & sc.exe config $service.Name binPath= ('"' + $binary + '"') start= delayed-auto
        if ($LASTEXITCODE -ne 0) { throw "Service update failed: $($service.Name)" }
    }
    & sc.exe failure $service.Name reset= 86400 actions= restart/60000/restart/60000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw "Service recovery policy failed: $($service.Name)" }
}

if ($Start) {
    foreach ($name in @("AStockMonitor.Api")) {
        Start-Service -Name $name
        (Get-Service -Name $name).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    }
}

Get-Service -Name ($services.Name) | Select-Object Name, Status, StartType
