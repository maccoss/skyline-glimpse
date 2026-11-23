# Hardklor.exe reference output

Expected results for `HardklorTests.AgreesWithHardklorExe`, made once with Skyline's Hardklor.exe
(v2.3.6, `pwiz_tools/Skyline/bin/x64/Release/Hardklor.exe`). The tests never run Hardklor.exe.

## Input

The 14 MS1 scans of pwiz's `example_data/small.RAW` (LTQ-FT), centroided by the Thermo library:

    msconvert small.RAW --mzML --64 --filter "peakPicking vendor msLevel=1-" --filter "msLevel 1" --outfile small_ms1_centroid.mzML

with pwiz-sharp's msconvert (the same reader Glimpse uses). The test reads `small.RAW` through
`PwizMsDataSource.ReadSpectrum(i, centroid: true)`; the peaks were checked to be identical to the mzML's.

## Runs

    Hardklor.exe hk_orbitrap.conf     -> small_orbitrap.hk
    Hardklor.exe hk_fticr_pwiz.conf   -> small_fticr_pwizabund.hk

- `hk_orbitrap.conf` is the configuration Skyline's `HardklorSearchEngine.cs` writes, with its defaults
  (Orbitrap, resolution 60,000, minimum idotp 0.9 so correlation 0.987688340595138, charge 1 to 7, depth 2).
  Skyline also writes an `isotope_data` file of its traditional abundances; for H, C, N, O and S those are
  Hardklor's built-in values, so the file is left out.
- `hk_fticr_pwiz.conf` is the same with FTICR at 100,000 (the LTQ-FT's analyzer) and
  `isotope_data = ISOTOPE_pwiz.DAT`: Hardklor's ISOTOPE.DAT with the H, C, N, O and S entries (and their
  Hx..Sx copies) replaced by pwiz-sharp's NIST abundances, the ones `PwizEnvelopeModel` uses
  (13C 0.0107 against Skyline's 0.01084). This isolates the port from the isotope abundances.
