<#
    Builds the SwissTechKnife module package:
      artifacts/module/clicket-<version>.zip
        module.json, Clicket.Module.dll       (loaded by the host)
        app/Clicket.Hosted.exe                (the app, run in its own process with --hosted)
    The SDK dll is never packaged: the host provides it. Prints sha256 and size for the host catalog.
    This file stays pure ASCII: Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI.
#>
[CmdletBinding()]
param([switch]$SkipAppBuild)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$moduleProject = Join-Path $root 'src\Clicket.Module\Clicket.Module.csproj'
$manifestPath = Join-Path $root 'src\Clicket.Module\module.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
if ($manifest.version -ne $version) { throw "module.json version $($manifest.version) differs from VERSION $version." }
if (-not (@($manifest.releaseNotes) | Where-Object { $_.version -eq $version })) { throw "module.json has no releaseNotes entry for $version." }

if (-not $SkipAppBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests }
dotnet build $moduleProject -c Release --nologo -v q
if ($LASTEXITCODE) { throw 'Module build failed.' }

$moduleOut = Join-Path $root 'src\Clicket.Module\bin\Release\net48'
$appExe = Join-Path $root 'src\Clicket\bin\Release\net48\Clicket.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw "Clicket.exe not found: $appExe" }

$outDir = Join-Path $root 'artifacts\module'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zipPath = Join-Path $outDir "$($manifest.id)-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }

$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    function Add-Entry([string]$file, [string]$entry) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entry, 'Optimal') | Out-Null
    }
    Add-Entry (Join-Path $moduleOut 'module.json') 'module.json'
    Add-Entry (Join-Path $moduleOut 'Clicket.Module.dll') 'Clicket.Module.dll'
    # The hosted copy gets its own image name, so it is never mistaken for the standalone app.
    Add-Entry $appExe 'app/Clicket.Hosted.exe'
    $config = "$appExe.config"
    if (Test-Path -LiteralPath $config) { Add-Entry $config 'app/Clicket.Hosted.exe.config' }
} finally { $zip.Dispose() }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zipPath).Length
Write-Host "Packed $zipPath" -ForegroundColor Green
Write-Host "  version $version"
Write-Host "  sha256  $hash"
Write-Host "  size    $size"
[pscustomobject]@{ Id = $manifest.id; Version = $version; File = $zipPath; Sha256 = $hash; Size = $size }
