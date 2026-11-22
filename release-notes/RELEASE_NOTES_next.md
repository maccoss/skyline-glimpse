# Skyline Glimpse v26.1.0 Release Notes

The first release: a QualBrowser-style raw-data browser for the open Skyline document, showing which
signal belongs to which peptide.

## New Features

- TIC and base-peak chromatograms for all scans, MS1, MS2 or a single isolation window, in ions/s or ions
  (intensity x injection time).
- m/z-by-retention-time heatmaps of intensity, document peptides per spectrum, and fraction of signal
  assigned to document peptides.
- Click any chromatogram or heatmap point to see the spectrum, with the ions of every document peptide in
  it labeled and colored by peptide. The label source can be the document's transitions, the library
  spectrum, or all ions of the chosen types and charges.
- XICs for any number of selected peptides or proteins (no top-N limit), summed, per protein or per
  peptide. Clicking a peptide's trace opens its spectrum with that peptide in focus.
- A properties panel with the file, acquisition, focused-peptide and library-match details.
- Follows the selection in Skyline's Targets view.
- The chromatogram is drawn while a data file is read, so a long run shows its TIC from the first seconds.
- On connecting, Glimpse opens the data file of the replicate Skyline is showing.
- MS1 peaks are labeled with the peptide and charge they belong to ("LMSPTGGR 2+ M+1"), not just the isotope.
- MS2 signal is assigned by the document's DIA isolation scheme, as Skyline extracts it: a precursor in the
  margin between two windows counts in one of them, and a set precursor filter or All Ions scheme is honoured.
  With an Overlap scheme each scan's own window is used, as Skyline does (Glimpse does not demultiplex).
- Several proteins, peptides from different proteins, or every protein (Ctrl+click, Shift+click, Ctrl+A),
  kept in step with Skyline's selection both ways.
- Plots are styled for figures: larger axis text, heavier axes and ticks, thicker traces and spectrum sticks,
  drawn at the screen's scale on high-DPI displays.

## Performance

- The document loads in 4.5 s instead of 45 s, and a file's integrated peaks in 3 s instead of 40 s, on a
  69,000-precursor, 620,000-transition, 17-replicate document. Glimpse no longer asks Skyline for locator
  columns (36 s a report) and reads reports as Parquet rather than CSV.
- A file's peaks are fetched before it is read, so the document's signals are measured in the same pass
  over the file instead of a second one (about a minute saved on an Astral run).
- A Thermo .raw file is read on several threads (up to 6, a quarter of the processor's cores): about
  50 s for a 180,000-spectrum Astral run instead of 80 s.
- XICs for a selection of more than 200 peptides are built in the background, so the window stays usable
  while every peptide of a large document is summed.
