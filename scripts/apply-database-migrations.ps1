param(
    [string]$MySqlRootPassword = "root-change-me"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Docker = (Get-Command docker.exe -ErrorAction SilentlyContinue).Source
if (-not $Docker) {
    $Docker = Join-Path $env:LOCALAPPDATA "Programs\DockerDesktop\resources\bin\docker.exe"
}
if (-not (Test-Path $Docker)) {
    throw "Docker CLI was not found."
}

$MigrationFiles = Get-ChildItem (Join-Path $Root "database\migrations") -Filter "*.sql" |
    Sort-Object Name
foreach ($Migration in $MigrationFiles) {
    Write-Host "Applying $($Migration.Name)..."
    Get-Content -Raw -Encoding utf8 $Migration.FullName |
        & $Docker exec -i astock-mysql mysql -uroot "-p$MySqlRootPassword"
    if ($LASTEXITCODE -ne 0) {
        throw "Migration failed: $($Migration.Name)"
    }
}

Write-Host "Database migrations completed."

