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
- Plot text and lines grow with the plot: a maximized window, or a large screen at 100% scaling, shows the
  same figure scaled up (up to 2.5 times) instead of small text and hairline traces on a large canvas.

## Bug Fixes

## Performance
