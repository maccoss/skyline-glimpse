# Skyline Glimpse vNEXT Release Notes

One-sentence summary of the release (written at release time).

## New Features

- The peptides-per-spectrum heatmap is drawn on a linear scale, as in Skyline-PRISM: it counts a few to a few
  tens of peptides, which a log scale only compressed. Log scale now applies to the intensity heatmap only, and
  is greyed out for the other values.
- The peptides-per-spectrum and assigned-fraction heatmaps use the Turbo color scale (Skyline-PRISM's
  high-contrast option), so sparse values stand out.
- The intensity heatmap is linear by default, in Viridis, with the top of its color scale at the 95th
  percentile of the cells with signal: the brightest 5% saturate instead of leaving the rest dark. Log scale
  is still available. Color bar labels are capitalized and simpler ("Intensity (Log10)", "Peptides per
  spectrum", "Fraction of signal assigned").
- Ions are calibrated: with Units set to Ions, each scan's intensity x injection time is divided by the α of
  the analyzer that recorded it (Hsu et al., J. Proteome Res. 2025, Table 1), so ion counts compare across
  instruments. The Orbitrap Astral (Zoom), Exploris 480, Ascend, Fusion Lumos and Stellar have published
  values; other instruments, and files whose instrument model is unknown, use α 1. The heatmap divides each analyzer's scans by its own α, also where one level mixes
  analyzers. The Properties panel shows each scan's analyzer and α.
- A Peptide-like chromatogram trace: the signal in isotope envelopes that persist over several scans, found
  while the file is read with a port of Hardklor and linked with Bullseye's Kronik (MS1, and MS2 within each
  DIA isolation window). Next to the TIC and the Assigned trace it shows how much signal looks like peptides
  and how much of that the document explains. Settings can count only multiply charged MS1 envelopes. Ion trap
  scans are not searched. In MS1 and MS2 spectra, peptide-like envelopes no document peptide explains are drawn
  in black and labeled with their monoisotopic m/z and charge (`542.29 2+`; Settings sets the decimals). The search is saved beside the raw file as `run.hk` (Parquet: every isotope envelope
  and persistent feature), so reopening the file skips it.
- A Settings window (toolbar *Settings...*) sets font sizes, axis frame and tick sizes, and line widths for
  every plot, and edits the ion calibration table. Settings are saved for the next session.
- Plot text and lines grow with the plot: a maximized window, or a large screen at 100% scaling, shows the
  same figure scaled up (up to 2.5 times) instead of small text and hairline traces on a large canvas.

## Bug Fixes

## Performance
