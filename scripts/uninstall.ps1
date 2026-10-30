[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifestPath = Join-Path $repositoryRoot 'build\native-host.json'
$backupRoot = Join-Path $repositoryRoot 'backups'
$hostName = 'jp.secretime.host'
$registryPrefixes = @('SOFTWARE\Google\Chrome', 'SOFTWARE\Microsoft\Edge',
    'SOFTWARE\BraveSoftware\Brave-Browser', 'SOFTWARE\Vivaldi')

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

function Assert-NoLinkedBackupPath {
    param([string] $Path)
    $current = [IO.Path]::GetFullPath($Path)
    if (-not $current.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must stay inside the repository.' }
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Backup path must be an ordinary directory: $current"
            }
        }
        if ($current -eq $repositoryRoot) { break }
        $current = Split-Path -Parent $current
    }
}

if ($env:OS -ne 'Windows_NT') { throw 'Native Messaging registration requires Windows.' }
$registrations = @()
foreach ($prefix in $registryPrefixes) {
    $registration = Read-Registration ($prefix + '\NativeMessagingHosts\' + $hostName)
    if (-not $registration.keyExisted -or $registration.isEmpty) { continue }
    if (-not $registration.defaultExisted -or -not (Test-OwnManifestPath $registration.value)) {
        throw "An unrecognized host registration exists; nothing was changed: HKCU\$($registration.subKey)"
    }
    $registrations += $registration
}
Write-Host 'First disable/remove SecretIME in every browser and close its private windows.'
Write-Host 'This script does not change IME settings. Review Google Japanese Input Properties > Privacy and restore your original mode manually if needed.'
Write-Host 'Files and recovery markers are retained. No files or registry subkeys are recursively deleted.'
if ($registrations.Count -eq 0) {
    Write-Host 'No matching current-user Native Messaging registrations were found.'
    return
}
$backupDirectory = Join-Path $backupRoot ((Get-Date).ToString('yyyyMMdd-HHmmss-fffffff') +
    '-uninstall-' + [Guid]::NewGuid().ToString('N'))
Assert-NoLinkedBackupPath $backupDirectory
if (-not $PSCmdlet.ShouldProcess('Matching HKCU SecretIME NativeMessagingHosts default values',
        'Back up and remove only registrations pointing to this repository')) { return }

[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backupPath = Join-Path $backupDirectory 'registrations-before.json'
$json = ConvertTo-Json -InputObject @($registrations) -Depth 8
[IO.File]::WriteAllText($backupPath, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
Write-Host "Backup: $backupDirectory"
foreach ($registration in $registrations) {
    $current = Read-Registration $registration.subKey
    if (-not $current.defaultExisted) { continue }
    if (-not (Test-OwnManifestPath $current.value)) {
        throw "Host registration changed during removal; preserved it. Backup: $backupDirectory"
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registration.subKey, $true)
    try {
        # Recheck through the writable handle; never remove another install's registration.
        $value = $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if (-not (Test-OwnManifestPath $value)) { throw 'The registration changed; it was preserved.' }
        $key.DeleteValue('', $false)
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
Write-Host 'Matching default values were removed; other values, empty keys and all files were retained.'
Write-Host "To restore registration, inspect $backupPath and restore only its recorded default values as REG_SZ after checking the host path."
Write-Host 'Re-running install.ps1 is also possible after reviewing any retained/foreign registry entries.'
