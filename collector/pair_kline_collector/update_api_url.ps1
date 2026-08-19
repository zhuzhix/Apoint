[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.local.json'
}

$configFullPath = (Resolve-Path -LiteralPath $ConfigPath).Path
$uri = [Uri]$ApiBaseUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http', 'https')) {
    throw 'ApiBaseUrl must be an absolute HTTP or HTTPS URL.'
}

$acl = Get-Acl -LiteralPath $configFullPath
$payload = Get-Content -LiteralPath $configFullPath -Raw | ConvertFrom-Json
$payload.apiBaseUrl = $uri.AbsoluteUri.TrimEnd('/')
$parent = Split-Path -Parent $configFullPath
$temporary = Join-Path $parent ('.collector-config-{0}.tmp' -f [Guid]::NewGuid())
try {
    [IO.File]::Create($temporary).Dispose()
    Set-Acl -LiteralPath $temporary -AclObject $acl
    [IO.File]::WriteAllText(
        $temporary,
        ($payload | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $configFullPath -Force
    $temporary = $null
    Write-Output "Collector API URL updated: $($payload.apiBaseUrl)"
}
finally {
    if ($null -ne $temporary -and (Test-Path -LiteralPath $temporary)) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
