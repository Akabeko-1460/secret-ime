[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $ExtensionId,
    [switch] $EnableAutomation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$buildDirectory = Join-Path $repositoryRoot 'build'
$manifestPath = Join-Path $buildDirectory 'native-host.json'
$settingsPath = Join-Path $buildDirectory 'host-settings.json'
$executablePath = Join-Path $buildDirectory 'SecretIME.Host.exe'
$backupRoot = Join-Path $repositoryRoot 'backups'
$hostName = 'jp.secretime.host'
$registryPrefixes = @('SOFTWARE\Google\Chrome', 'SOFTWARE\Microsoft\Edge',
    'SOFTWARE\BraveSoftware\Brave-Browser', 'SOFTWARE\Vivaldi')
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-NoLinkedPath {
    param([string] $Path)
    $current = [IO.Path]::GetFullPath($Path)
    if (-not $current.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside the repository.' }
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked installation path: $current"
            }
        }
        if ($current -eq $repositoryRoot) { break }
        $current = Split-Path -Parent $current
    }
}

function Test-OwnManifestPath {
    param($Value)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { return $false }
    try { return [IO.Path]::GetFullPath($Value).Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase) }
    catch { return $false }
}

function Read-Registration {
    param([string] $SubKey)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey, $false)
    try {
        $hasDefault = $null -ne $key -and @($key.GetValueNames()) -contains ''
        $defaultValue = $null
        $defaultKind = $null
        if ($hasDefault) {
            $defaultValue = $key.GetValue('', $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $defaultKind = $key.GetValueKind('').ToString()
        }
        return [pscustomobject]@{
            subKey = $SubKey; keyExisted = ($null -ne $key)
            defaultExisted = $hasDefault; value = $defaultValue; kind = $defaultKind
            isEmpty = ($null -ne $key -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0)
        }
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

function Read-OwnedRegistrations {
    $registrations = @()
    foreach ($prefix in $registryPrefixes) {
        $registration = Read-Registration ($prefix + '\NativeMessagingHosts\' + $hostName)
        if ($registration.keyExisted -and -not $registration.isEmpty -and
                (-not $registration.defaultExisted -or -not (Test-OwnManifestPath $registration.value))) {
            throw "An unrecognized host registration exists; nothing was changed: HKCU\$($registration.subKey)"
        }
        $registrations += $registration
    }
    return $registrations
}

function Save-ExistingFile {
    param([string] $Path, [string] $BackupDirectory)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected a file: $Path" }
    Copy-Item -LiteralPath $Path -Destination (Join-Path $BackupDirectory ([IO.Path]::GetFileName($Path)))
}

function Write-Json {
    param([string] $Path, $Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 8
    $stagedPath = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($stagedPath, $json + [Environment]::NewLine, $utf8)
    Move-Item -LiteralPath $stagedPath -Destination $Path -Force
}

if ($env:OS -ne 'Windows_NT') { throw 'Native Messaging registration requires Windows.' }
foreach ($path in @($buildDirectory, $manifestPath, $settingsPath, $executablePath, $backupRoot)) {
    Assert-NoLinkedPath $path
}
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw 'Build the host first with scripts/build.ps1. Installation does not build or download software.'
}
if ([string]::IsNullOrEmpty($ExtensionId)) {
    $extensionManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'extension\manifest.json') -Raw |
        ConvertFrom-Json
    if (-not $extensionManifest.PSObject.Properties['key']) { throw 'Supply -ExtensionId or a manifest public key.' }
    $publicKey = [Convert]::FromBase64String($extensionManifest.key)
    if ($publicKey.Length -eq 0) { throw 'The extension public key is empty.' }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $digest = $sha256.ComputeHash($publicKey) }
    finally { $sha256.Dispose() }
    $letters = 'abcdefghijklmnop'
    $ExtensionId = -join (0..15 | ForEach-Object {
        $letters[[int]($digest[$_] -shr 4)]
        $letters[[int]($digest[$_] -band 15)]
    })
}
if (-not [Text.RegularExpressions.Regex]::IsMatch($ExtensionId, '\A[a-p]{32}\z')) {
    throw 'ExtensionId must contain exactly 32 lowercase letters a-p.'
}
$origin = 'chrome-extension://' + $ExtensionId + '/'
$registrations = @(Read-OwnedRegistrations)
$nativeManifest = [ordered]@{
    name = $hostName; description = 'SecretIME experimental local IME mode assistant'
    path = $executablePath; type = 'stdio'; allowed_origins = @($origin)
}
$hostSettings = [ordered]@{
    automationEnabled = [bool]$EnableAutomation
    allowedOrigins = @($origin)
}
Write-Host "Extension origin: $origin"
Write-Host "Automation enabled: $([bool]$EnableAutomation) (default: disabled)"
Write-Warning 'Four primary browser keys and Chrome fallback compatibility are not real-browser verified.'
if (-not $PSCmdlet.ShouldProcess('HKCU NativeMessagingHosts for Chrome, Edge, Brave, Vivaldi and build JSON files',
        'Back up existing values/files and install SecretIME for the current user')) { return }

$backupDirectory = Join-Path $backupRoot ((Get-Date).ToString('yyyyMMdd-HHmmss-fffffff') +
    '-install-' + [Guid]::NewGuid().ToString('N'))
Assert-NoLinkedPath $backupDirectory
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$registrationBackupPath = Join-Path $backupDirectory 'registrations-before.json'
Write-Json $registrationBackupPath $registrations
Save-ExistingFile $manifestPath $backupDirectory
Save-ExistingFile $settingsPath $backupDirectory
Write-Host "Backup: $backupDirectory"

# Preflight all destinations again before replacing files or registry values.
$registrations = @(Read-OwnedRegistrations)
Write-Json $manifestPath $nativeManifest
Write-Json $settingsPath $hostSettings
foreach ($registration in $registrations) {
    $current = Read-Registration $registration.subKey
    if ($current.keyExisted -and -not $current.isEmpty -and
            (-not $current.defaultExisted -or -not (Test-OwnManifestPath $current.value))) {
        throw "Host registration changed during installation; stopped. Backup: $backupDirectory"
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registration.subKey)
    try { $key.SetValue('', $manifestPath, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}
Write-Host 'Registered for this Windows user only. No IME settings were changed by this script.'
Write-Host 'Load extension/ in each browser and explicitly allow private-window execution.'
Write-Host 'If installation was interrupted, inspect registrations-before.json and the saved JSON files before restoring.'
