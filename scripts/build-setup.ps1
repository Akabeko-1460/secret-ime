[CmdletBinding()]
param([string] $Version = '0.1.0-alpha.2')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]+)?\z') { throw 'Invalid version.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distribution = Join-Path $repositoryRoot 'dist'
$output = Join-Path $distribution ('SecretIME-' + $Version + '-Setup.exe')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Assert-OrdinaryPath([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current -eq $repositoryRoot -or $current.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing linked package path: $current"
        }
        if ($current -eq $repositoryRoot) { break }
        $current = Split-Path -Parent $current
    }
}

Assert-OrdinaryPath $output
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.sha256'))) {
    throw 'Setup or checksum already exists. Preserve it and choose another version.'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Existing .NET Framework x64 compiler is required.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadFiles = @('README.md', 'LICENSE', 'docs/install.md', 'build/SecretIME.Host.exe',
    'extension/manifest.json', 'extension/background.mjs', 'extension/controller.mjs',
    'extension/presentation.mjs', 'extension/popup.mjs', 'extension/popup.html',
    'extension/popup.css', 'extension/README.md')
foreach ($relative in $payloadFiles) {
    $source = Join-Path $repositoryRoot $relative
    Assert-OrdinaryPath $source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing input: $relative" }
}
$build = Join-Path $repositoryRoot 'build'
Assert-OrdinaryPath $build
$payload = Join-Path $build ('setup-payload-' + [Guid]::NewGuid().ToString('N') + '.zip')
$archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $payloadFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $repositoryRoot $relative),
            $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
[IO.Directory]::CreateDirectory($distribution) | Out-Null
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/codepage:65001',
    '/optimize+', '/warn:4', ('/out:' + $output), ('/win32manifest:' + (Join-Path $repositoryRoot 'setup/app.manifest')),
    ('/resource:' + $payload + ',SecretIME.Payload'), '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    (Join-Path $repositoryRoot 'setup/Installer.cs'), (Join-Path $repositoryRoot 'setup/Program.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = [IO.File]::Open($output + '.sha256', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes($hash + '  ' + [IO.Path]::GetFileName($output) + "`n")
    $checksum.Write($bytes, 0, $bytes.Length)
} finally { $checksum.Dispose() }
Write-Host "Setup: $output"
Write-Host "SHA256: $hash"
Write-Host 'Compilation only: Setup was not launched; no registration or IME operations were performed.'
