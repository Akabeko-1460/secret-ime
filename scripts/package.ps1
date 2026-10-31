[CmdletBinding()]
param([string] $Version = '0.1.0-alpha.2')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]+)?\z') { throw 'Invalid version.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distributionRoot = Join-Path $repositoryRoot 'dist'
$executable = Join-Path $repositoryRoot 'build\SecretIME.Host.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the host before packaging.' }
if ((Get-Item -LiteralPath $repositoryRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The package root must be an ordinary directory.'
}
if ((Test-Path -LiteralPath $distributionRoot) -and
    ((Get-Item -LiteralPath $distributionRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The output directory must not be a link.'
}
$archive = Join-Path $distributionRoot ('SecretIME-' + $Version + '-win-x64.zip')
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath ($archive + '.sha256'))) {
    throw 'An archive or checksum already exists. Preserve it and choose another version.'
}
$stage = Join-Path $distributionRoot ('staging-' + [Guid]::NewGuid().ToString('N') + '\SecretIME')
[IO.Directory]::CreateDirectory($stage) | Out-Null
$packageFiles = @(
    'README.md', 'LICENSE', 'build\SecretIME.Host.exe', 'docs\install.md',
    'extension\manifest.json', 'extension\background.mjs', 'extension\controller.mjs',
    'extension\presentation.mjs', 'extension\popup.mjs', 'extension\popup.html',
    'extension\popup.css', 'extension\README.md',
    'host\Core.cs', 'host\Program.cs', 'host\GoogleUiAdapter.cs',
    'scripts\build.ps1', 'scripts\install.ps1', 'scripts\uninstall.ps1', 'scripts\package.ps1',
    'scripts\build-setup.ps1', 'setup\Installer.cs', 'setup\Program.cs', 'setup\app.manifest',
    'docs\design.md', 'docs\validation.md', 'docs\native-contract.md',
    'tests\CoreTests.cs', 'tests\SetupTests.cs', 'tests\extension.test.mjs', 'tests\transport.test.mjs', 'tests\browser_smoke.py'
)
foreach ($relative in $packageFiles) {
    $source = Join-Path $repositoryRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $relative" }
    $current = $source
    while ($current -ne $repositoryRoot) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing linked package input.'
        }
        $current = Split-Path -Parent $current
    }
    $destination = Join-Path $stage $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
Compress-Archive -LiteralPath $stage -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumBytes = [Text.Encoding]::ASCII.GetBytes($hash + '  ' + [IO.Path]::GetFileName($archive) + [Environment]::NewLine)
$checksum = [IO.File]::Open($archive + '.sha256', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $checksum.Write($checksumBytes, 0, $checksumBytes.Length) }
finally { $checksum.Dispose() }
Write-Host "Package: $archive"
Write-Host "SHA256: $hash"
Write-Host 'No registration, IME operation, or file deletion was performed.'
