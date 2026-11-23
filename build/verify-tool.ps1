<#
  Smoke-test the packaged Skyline tool before shipping or installing it.

  Extracts SkylineGlimpse.zip to a clean directory (a fresh-install simulation) and launches
  SkylineGlimpse.exe with a dummy connection argument. The main window - and so ScottPlot/SkiaSharp and
  the pwiz-sharp assemblies - load during startup, so a missing or broken dependency shows up as an
  UNHANDLED entry in glimpse.log. The failed connection from the dummy argument is expected.

  Exit code 0 = launched and built its window cleanly; 1 = load failure or missing exe.

  Usage:
    pwsh build/verify-tool.ps1
    pwsh build/verify-tool.ps1 -Zip path\to\SkylineGlimpse.zip -WaitSeconds 60
#>
param(
    [string]$Zip = (Join-Path $PSScriptRoot '..\publish\SkylineGlimpse.zip'),
    [int]$WaitSeconds = 60
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Zip)) { Write-Host "VERIFY FAILED: zip not found: $Zip"; exit 1 }

$log = Join-Path $env:LOCALAPPDATA 'SkylineGlimpse\glimpse.log'
$startLen = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

$dir = Join-Path $env:TEMP 'glimpse-tool-verify'
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $dir)

$exe = Join-Path $dir 'SkylineGlimpse.exe'
if (-not (Test-Path $exe)) { Write-Host 'VERIFY FAILED: SkylineGlimpse.exe not found in the zip'; exit 1 }
foreach ($required in @('tool-inf\info.properties', 'tool-inf\SkylineGlimpse.properties', 'tool-inf\Glimpse.png', 'Pwiz.Vendor.Thermo.dll', 'ThermoFisher.CommonCore.RawFileReader.dll')) {
    if (-not (Test-Path (Join-Path $dir $required))) { Write-Host "VERIFY FAILED: $required missing from the zip"; exit 1 }
}
# Skyline takes the tool's logo from the one image directly in tool-inf; with none it shows the default logo.
$images = @(Get-ChildItem (Join-Path $dir 'tool-inf') -File | Where-Object { $_.Extension -in '.png', '.jpg', '.jpeg', '.gif' })
if ($images.Count -ne 1) { Write-Host "VERIFY FAILED: tool-inf must hold exactly one logo image (png, jpg or gif), found $($images.Count)"; exit 1 }

function Get-LogTail {
    if (-not (Test-Path $log)) { return '' }
    $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$fs.Seek($startLen, 'Begin')
        $sr = New-Object System.IO.StreamReader($fs)
        try { return $sr.ReadToEnd() } finally { $sr.Close() }
    } finally { $fs.Dispose() }
}

Write-Host "Launching (extracted) $exe ..."
$p = Start-Process -FilePath $exe -ArgumentList 'verify-smoke-no-connection' -PassThru
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    if ((Get-LogTail) -match 'UNHANDLED \(|MainWindow loaded') { break }
    if ($p.HasExited) { break }
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Seconds 2   # let a late startup failure reach the log
if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }

$tail = Get-LogTail
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue

if ($tail -match 'UNHANDLED \(') {
    Write-Host 'VERIFY FAILED: an exception escaped to the global handler on startup:'
    Write-Host $tail
    exit 1
}
if ($tail -notmatch 'MainWindow loaded') {
    Write-Host 'VERIFY FAILED: the main window never finished loading.'
    Write-Host $tail
    exit 1
}
Write-Host 'VERIFY PASSED: SkylineGlimpse.exe launched from the packaged zip and built its main window.'
exit 0
