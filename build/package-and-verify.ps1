<#
  Ship gate for the Skyline external tool: test -> package -> launch-verify. Any failing step aborts
  with a non-zero exit code.

  Usage:
    pwsh build/package-and-verify.ps1
    pwsh build/package-and-verify.ps1 -Configuration Debug
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# The two version sources (assemblies, Skyline tool manifest) must agree - see "Releases" in CLAUDE.md.
$props = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$manifest = (Get-Content (Join-Path $root 'src/Glimpse.App/tool-inf/info.properties') | Where-Object { $_ -match '^Version\s*=' }) -replace '^Version\s*=\s*', ''
if ($props.Trim() -ne $manifest.Trim()) {
    Write-Host "ABORT: Directory.Build.props Version ($props) differs from info.properties Version ($manifest)." -ForegroundColor Red
    exit 1
}
Write-Host "Version $props" -ForegroundColor Cyan

Write-Host '== 1/3  Tests ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'Glimpse.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: tests failed.' -ForegroundColor Red; exit 1 }

Write-Host '== 2/3  Package SkylineGlimpse.zip ==' -ForegroundColor Cyan
dotnet msbuild (Join-Path $root 'build\package.proj') "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: packaging failed.' -ForegroundColor Red; exit 1 }

Write-Host '== 3/3  Launch smoke test (extract zip + run the exe) ==' -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-tool.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: the packaged tool failed to launch.' -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host 'READY: publish\SkylineGlimpse.zip is tested, packaged and launch-verified.' -ForegroundColor Green
