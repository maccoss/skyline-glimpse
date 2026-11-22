# Skyline Glimpse - working rules

A Skyline External Tool (.NET 10, WPF, ScottPlot 5) for browsing raw data with document-aware peptide
signal assignment. See README.md for the feature list and layout.

## Rules

- **Reader seam.** Only `src/Glimpse.Pwiz` may reference pwiz types. Everything else uses
  `Glimpse.Core.Data.IMsDataSource` / `SpectrumData` / `ScanHeader`. This keeps the future switch to
  Skyline's own installed readers (`GlimpseReaders=Skyline` in `Pwiz.props`) to one project.
- **Skyline connection** (`Glimpse.Skyline.SkylineConnection`): derive the JSON pipe name from
  `$(SkylineConnection)` with `JsonToolConstants.GetJsonPipeName`, open a NEW pipe per call, set
  `PipeTransmissionMode.Message`. Breaking any of the three gives "0x00 is invalid start of value".
  The JSON API has no push events: follow the Skyline selection by polling.
- **Reports.** Never select the ProteinLocator/PeptideLocator/PrecursorLocator columns: they cost Skyline about
  0.5 ms a row (36 s on a 69,000-precursor document). Build locators with `SkylineLocators` from the name
  columns. Export reports as `.parquet` (3.5 s against 12 s for CSV) and read them with
  `SkylineDocumentLoader.ReadReport`, which falls back to CSV for an older Skyline.
- **Culture.** Never set `InvariantGlobalization=true` (WPF and the Thermo SDK need real cultures).
  Pass `CultureInfo.InvariantCulture` explicitly whenever parsing or formatting data, and request
  reports with culture `"invariant"`.
- **Units.** Retention times are minutes, injection times milliseconds. Ion counts are
  `intensity * injectionTimeMs / 1000` (Skyline's `LcPeakIonMetrics` convention). A scan without an
  injection time has no ion count (NaN), never a guessed one.
- **One pass.** Anything that needs every spectrum's peaks is an `IScanObserver` riding on
  `ScanIndexBuilder.Build`, not a second walk over the file.
- **Vendored code.** `external/SkylineTool/*` is copied verbatim from pwiz; re-copy, don't edit.
  Code ported from Skyline-PRISM keeps a comment naming its origin.
- Packaging: `pwsh build/package-and-verify.ps1`. Close a running Glimpse before reinstalling the zip.

## Releases

Skyline-PRISM's scheme: CalVer `YY.feature.patch` (`26.1.0`), bumped only at release time, tag `v{version}`.
Notes follow `release-notes/README.md`: append to `RELEASE_NOTES_next.md` as changes land.

1. Rename `RELEASE_NOTES_next.md` to `RELEASE_NOTES_v{version}.md`, fix its heading, drop empty sections,
   start a fresh `RELEASE_NOTES_next.md`.
2. Set the version in BOTH `Directory.Build.props` `<Version>` and `src/Glimpse.App/tool-inf/info.properties`
   `Version =`. `VersionTests` and `package-and-verify.ps1` fail when they differ.
3. `pwsh build/package-and-verify.ps1` (tests, zip, launch check). The zip is built locally: it needs the
   pwiz-sharp checkout, which CI does not have.
4. Commit, squash-merge, tag `v{version}`, attach `publish/SkylineGlimpse.zip` and the notes to a GitHub Release.

## Testing

- Every change to logic comes with a test that fails without the change. Run `dotnet test Glimpse.sln`.
- Coverage: `pwsh build/coverage.ps1` (Glimpse-owned code, vendored client excluded). Keep the Core,
  Library, Plotting and Pwiz projects above 90% of lines.
- **Coverage is not enough.** `pwsh build/mutation.ps1` runs Stryker.NET, which plants small bugs and
  reports any no test caught. Run it on the projects you touched before calling a change done, read the
  survivors in `build/mutation/<project>/reports/mutation-report.html`, and either add a test that
  kills each meaningful survivor or note why it is equivalent (cannot change behavior).
- Assert literal expected values, never the code's own constants (comparing an array with itself
  proves nothing - mutation testing found exactly that once).
- Test doubles live in `tests/Glimpse.Tests/Fakes.cs` (in-memory run, documents, library) and
  `SkylineConnectionTests.cs` (`FakeSkylineServer`, which speaks Skyline's real pipe protocol).

## Test data

- pwiz Thermo test files (`BSA-FT-HCD.raw`, `BSA-FT-ETD.raw`, `small.RAW`) are found through
  `PwizSharpDir`; see `tests/Glimpse.Tests/TestData.cs`.
- `GLIMPSE_BIG_RAW` (+ optional `GLIMPSE_BIG_RAW_MAX`) times a pass over a real run.
