<#
.SYNOPSIS
  Publishes svacitra as a self-contained, single-file svacitra.exe (win-x64).

.PARAMETER SkipTests
  Publish without running the unit tests first.

.PARAMETER ArtifactsPath
  Optional folder for intermediate build output (bin/obj). Useful when the source lives on a synced
  drive (Google Drive, OneDrive) and you do not want build junk synced.

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -SkipTests -ArtifactsPath "$env:TEMP\picasa-revive-build"
#>
param(
    [switch]$SkipTests,
    [string]$ArtifactsPath,
    [string]$InstallerCompiler
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$extra = @()
if ($ArtifactsPath) { $extra += @('--artifacts-path', $ArtifactsPath) }

if (-not $SkipTests) {
    Write-Host '=== Running unit tests ===' -ForegroundColor Cyan
    dotnet test PicasaRevive.Tests/PicasaRevive.Tests.csproj -c Release -r win-x64 -p:SelfContained=true --nologo @extra
    if ($LASTEXITCODE -ne 0) {
        throw 'Tests failed or could not run. If the output mentions "Application Control policy", Windows Smart App Control is blocking unsigned local builds (see README.md).'
    }
}

Write-Host '=== Publishing svacitra.exe ===' -ForegroundColor Cyan
dotnet publish PicasaRevive.App\PicasaRevive.App.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish --nologo @extra
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Get-Item (Join-Path $PSScriptRoot 'publish\svacitra.exe')
Write-Host ("Done: {0}  ({1:N1} MB, {2})" -f $exe.FullName, ($exe.Length / 1MB), $exe.LastWriteTime) -ForegroundColor Green

if ($InstallerCompiler) {
    & $InstallerCompiler /Qp (Join-Path $PSScriptRoot 'installer/PicasaRevive.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
