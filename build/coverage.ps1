<#
  Run the test suite with line coverage and print a per-project, per-class summary.

  Vendored code (external/SkylineTool, the generated regex helpers) is excluded from the totals: its
  coverage says nothing about Glimpse.

  Usage:
    pwsh build/coverage.ps1                 # summary, classes under 90% listed
    pwsh build/coverage.ps1 -MinimumPercent 90   # also fail when any Glimpse project is below 90%
#>
param([int]$MinimumPercent = 0)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$results = Join-Path $root 'tests\Glimpse.Tests\TestResults'
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue

dotnet test (Join-Path $root 'tests\Glimpse.Tests') --collect:"XPlat Code Coverage" -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'Tests failed.' -ForegroundColor Red; exit 1 }

$file = Get-ChildItem $results -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
[xml]$xml = Get-Content $file.FullName
$failed = $false
foreach ($package in $xml.coverage.packages.package) {
    if (-not $package.name.StartsWith('Glimpse')) { continue }
    $classes = @{}
    foreach ($class in $package.classes.class) {
        if ($class.filename -match 'external[\\/]SkylineTool' -or $class.name -match 'RegularExpressions') { continue }
        $name = ($class.name -split '[<`/]')[0]
        $lines = @($class.lines.line)
        if (-not $classes.ContainsKey($name)) { $classes[$name] = @(0, 0) }
        $classes[$name][0] += @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        $classes[$name][1] += $lines.Count
    }
    $covered = ($classes.Values | ForEach-Object { $_[0] } | Measure-Object -Sum).Sum
    $total = ($classes.Values | ForEach-Object { $_[1] } | Measure-Object -Sum).Sum
    if ($total -eq 0) { continue }
    $percent = [math]::Round(100 * $covered / $total, 1)
    $color = if ($percent -ge 90) { 'Green' } elseif ($percent -ge 75) { 'Yellow' } else { 'Red' }
    Write-Host ("{0,-18} {1,6}%  ({2}/{3} lines)" -f $package.name, $percent, $covered, $total) -ForegroundColor $color
    foreach ($entry in $classes.GetEnumerator() | Sort-Object { $_.Value[0] / [math]::Max(1, $_.Value[1]) }) {
        $p = 100 * $entry.Value[0] / [math]::Max(1, $entry.Value[1])
        if ($entry.Value[1] -gt 0 -and $p -lt 90) { Write-Host ("    {0,5:N0}%  {1}" -f $p, $entry.Key) }
    }
    if ($MinimumPercent -gt 0 -and $percent -lt $MinimumPercent) { $failed = $true }
}
Write-Host "Full report: $($file.FullName)"
if ($failed) { Write-Host "Coverage below $MinimumPercent%." -ForegroundColor Red; exit 1 }
