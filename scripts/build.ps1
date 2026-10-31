[CmdletBinding()]
param([switch] $Test)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$buildDirectory = Join-Path $repositoryRoot 'build'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$wpfDirectory = Join-Path $frameworkDirectory 'WPF'

function Assert-NoLinkedOutputDirectory {
    param([string] $Directory)
    $current = [IO.Path]::GetFullPath($Directory)
    while ($current.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or $current -eq $repositoryRoot) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Build output directory must be an ordinary directory: $current"
            }
        }
        if ($current -eq $repositoryRoot) { break }
        $current = Split-Path -Parent $current
    }
}

function Invoke-Compilation {
    param([string] $OutputName, [string] $MainClass, [string[]] $Sources)
    $outputPath = Join-Path $buildDirectory $OutputName
    if (Test-Path -LiteralPath $outputPath) {
        $existing = Get-Item -LiteralPath $outputPath -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked or non-file build output: $outputPath"
        }
        $backupDirectory = Join-Path $buildDirectory ('backups\' + $backupStamp)
        Assert-NoLinkedOutputDirectory $backupDirectory
        [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
        Copy-Item -LiteralPath $outputPath -Destination (Join-Path $backupDirectory $OutputName)
        Write-Host "Backup: $backupDirectory"
    }
    $stagedPath = Join-Path $buildDirectory (([Guid]::NewGuid().ToString('N')) + '.exe')
    $arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/codepage:65001',
        '/optimize+', '/warn:4', ('/main:' + $MainClass), ('/out:' + $stagedPath))
    $arguments += $references
    $arguments += $Sources
    & $compiler @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed: $OutputName" }
    Move-Item -LiteralPath $stagedPath -Destination $outputPath -Force
    return $outputPath
}

if ($env:OS -ne 'Windows_NT' -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'An existing Windows x64 .NET Framework 4 compiler is required. No dependencies are installed.'
}
$references = @('/r:System.Web.Extensions.dll')
foreach ($assembly in @('WindowsBase.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll',
        'PresentationCore.dll', 'PresentationFramework.dll')) {
    $assemblyPath = Join-Path $wpfDirectory $assembly
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Required existing .NET Framework assembly is missing: $assemblyPath"
    }
    $references += '/r:' + $assemblyPath
}
$hostSources = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'host') -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
if ($hostSources.Count -eq 0) { throw 'No host C# sources were found.' }
Assert-NoLinkedOutputDirectory $buildDirectory
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
$backupStamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N')
$hostExecutable = Invoke-Compilation 'SecretIME.Host.exe' 'SecretIme.Program' $hostSources
Write-Host "Built: $hostExecutable"

if ($Test) {
    $node = Get-Command node -CommandType Application -ErrorAction Stop
    & $node.Source --test (Join-Path $repositoryRoot 'tests\extension.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser extension tests failed.' }
    $coreSources = @((Join-Path $repositoryRoot 'host\Core.cs'),
        (Join-Path $repositoryRoot 'tests\CoreTests.cs'))
    $testExecutable = Invoke-Compilation 'SecretIME.CoreTests.exe' 'CoreTests' $coreSources
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Core/protocol tests failed.' }
    $setupSources = @((Join-Path $repositoryRoot 'setup\Installer.cs'),
        (Join-Path $repositoryRoot 'tests\SetupTests.cs'))
    $references += @('/r:System.Core.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
    $setupTests = Invoke-Compilation 'SecretIME.SetupTests.exe' 'SetupTests' $setupSources
    & $setupTests
    if ($LASTEXITCODE -ne 0) { throw 'Isolated setup tests failed.' }
    Write-Host 'Automated tests passed. Real IME/browser protection remains unverified.'
}
