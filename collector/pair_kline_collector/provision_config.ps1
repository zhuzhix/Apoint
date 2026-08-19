[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GatewayKeyPath,
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$ConfigPath,
    [string]$CollectorId = 'local-pair-kline-01',
    [Security.SecureString]$GmToken
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.local.json'
}

if ($null -eq $GmToken) {
    $GmToken = Read-Host 'Enter gm token (input is hidden)' -AsSecureString
}

$gatewayPath = (Resolve-Path -LiteralPath $GatewayKeyPath).Path
$gatewayKey = [IO.File]::ReadAllText($gatewayPath).Trim()
if ($gatewayKey.Length -lt 32) {
    throw 'Gateway key is too short; refusing to create production config.'
}

$bstr = [IntPtr]::Zero
$temporary = $null
try {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($GmToken)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'The gm token cannot be empty.'
    }

    $configFullPath = [IO.Path]::GetFullPath($ConfigPath)
    $parent = Split-Path -Parent $configFullPath
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $payload = [ordered]@{
        apiBaseUrl = $ApiBaseUrl.TrimEnd('/')
        gatewayApiKey = $gatewayKey
        gmToken = $token.Trim()
        collectorId = $CollectorId
        pollSeconds = 20
        heartbeatSeconds = 10
        symbolsPerSdkRequest = 20
        maxPushBars = 2000
        requestTimeoutSeconds = 30
        stateDirectory = 'runtime'
        provider = 'gm'
    }
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $identities = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
    foreach ($identity in $identities) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    $json = $payload | ConvertTo-Json -Depth 4
    $temporary = Join-Path $parent ('.collector-config-{0}.tmp' -f [Guid]::NewGuid())
    [IO.File]::Create($temporary).Dispose()
    Set-Acl -LiteralPath $temporary -AclObject $acl
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $configFullPath -Force
    $temporary = $null
    Write-Output "Collector private config created with restricted ACL: $configFullPath"
}
finally {
    if ($null -ne $temporary -and (Test-Path -LiteralPath $temporary)) {
        Remove-Item -LiteralPath $temporary -Force
    }
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    $token = $null
    $gatewayKey = $null
}
