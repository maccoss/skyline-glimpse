# Skyline Glimpse

A raw-data browser for the open Skyline document, in the spirit of Thermo QualBrowser / FreeStyle and
SeeMS, that also shows **which signals belong to which peptides and proteins in the document**.

- **Chromatograms:** TIC and base peak, over MS1, MS2, all scans or one isolation window, in
  intensity (ions/s for Orbitrap/Astral) or ion counts (intensity × injection time).
- **Heatmaps:** m/z by retention time, for MS1 (binned peaks) and MS2 (each scan's signal across its
  isolation window).
- **Spectra:** click any point on a chromatogram or heatmap to show that spectrum, step with the arrow
  keys, and read its acquisition details and statistics in the properties panel.
- *(in progress)* Fragment labels for every document peptide in a spectrum, peaks colored by peptide,
  per-spectrum assigned-signal fraction, peptides-per-spectrum heatmaps, and XICs for any number of
  selected peptides or proteins.

## Build

Glimpse is .NET 10 / WPF / ScottPlot 5. It reads instrument files with
[pwiz-sharp](https://github.com/ProteoWizard/pwiz), the .NET port of ProteoWizard, which it expects as
a sibling checkout:

```
<parent>/
  maccoss/skyline-glimpse/          (this repository)
  ProteoWizard/pwiz/pwiz-sharp/     (the reader)
```

Point elsewhere with `-p:PwizSharpDir=<path>/pwiz/pwiz-sharp`. The Thermo SDK is license-gated;
`Directory.Build.rsp` passes `-p:IAgreeToVendorLicenses=true` to every build.

```powershell
dotnet build Glimpse.sln
dotnet test Glimpse.sln
pwsh build/package-and-verify.ps1     # test, package publish/SkylineGlimpse.zip, launch-verify
```

`GLIMPSE_BIG_RAW=<path to a .raw>` makes the test suite time a full pass over a real run.

### Test quality

```powershell
pwsh build/coverage.ps1      # line coverage of Glimpse's own code, per project and class
pwsh build/mutation.ps1      # Stryker.NET mutation testing: do the tests catch planted bugs?
```

Coverage shows which code the tests ran; the mutation score shows whether a wrong answer in that code
would have failed a test. Reports land in `build/mutation/<project>/reports/`.

## Install in Skyline

Tools > External Tools > Add > From File..., and choose `publish/SkylineGlimpse.zip`. Requires the
.NET 10.0 Desktop Runtime. Glimpse then appears on the Tools menu and connects to the document it was
launched from.

Glimpse also runs standalone: `SkylineGlimpse.exe <data file>`, or Open file... in the window.

## Vendor readers

pwiz-sharp and the Thermo reader are **bundled for now**. Skyline will soon ship on .NET 10 with
pwiz-sharp and every vendor reader in its own installation; Glimpse will then load those from the
running Skyline instead of carrying copies (`-p:GlimpseReaders=Skyline`, see `Pwiz.props`). Only
`Glimpse.Pwiz` touches pwiz types, so that switch is confined to one project.

## Layout

| Project | |
|---|---|
| `src/Glimpse.Core` | Pure logic: data abstractions, scan index, chromatogram and heatmap builders |
| `src/Glimpse.Pwiz` | The reader seam: `IMsDataSource` over pwiz-sharp |
| `src/Glimpse.Skyline` | Skyline JSON-RPC connection (vendored client in `external/SkylineTool`) |
| `src/Glimpse.App` | WPF window, ScottPlot views, tool-inf manifest |
| `tests/Glimpse.Tests` | xUnit, using pwiz's Thermo test files |
