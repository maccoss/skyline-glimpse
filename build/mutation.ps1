<#
  Mutation testing with Stryker.NET: plants small bugs (flipped comparisons, off-by-ones, removed
  statements, changed constants) in a project, runs the tests against each, and reports how many the
  tests caught ("killed"). A surviving mutant is a change to the code that no test noticed.

  Coverage says code ran; the mutation score says a wrong answer would have failed a test.

  Usage:
    pwsh build/mutation.ps1                       # Core, Library, Plotting, Skyline loader
    pwsh build/mutation.ps1 -Projects Glimpse.Core
    pwsh build/mutation.ps1 -Break 80             # fail when any project scores below 80%

  Reports: build/mutation/<project>/reports/mutation-report.html
#>
param(
    [string[]]$Projects = @('Glimpse.Core', 'Glimpse.Library', 'Glimpse.Plotting', 'Glimpse.Skyline'),
    [int]$Break = 0
)
$ErrorActionPreference = 'Stop'
# "pwsh -File mutation.ps1 -Projects A,B" passes "A,B" as one string.
$Projects = @($Projects | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location (Join-Path $root 'tests\Glimpse.Tests')
try {
    dotnet tool restore | Out-Null
    $failed = @()
    foreach ($project in $Projects) {
        $out = Join-Path $root "build\mutation\$($project.Replace('Glimpse.', '').ToLowerInvariant())"
        Write-Host "== $project ==" -ForegroundColor Cyan
        $args = @('stryker', '--project', "$project.csproj", '--output', $out, '--break-at', $Break)
        # The vendored Skyline client is pwiz's code, not Glimpse's.
        if ($project -eq 'Glimpse.Skyline') { $args += @('--mutate', '!**/external/**') }
        dotnet @args
        if ($LASTEXITCODE -ne 0) { $failed += $project }
    }
    if ($failed.Count -gt 0) { Write-Host "Below the break threshold: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
}
finally {
    Pop-Location
}
