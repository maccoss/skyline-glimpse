# Release Notes

Per-version release notes for Skyline Glimpse. The convention is Skyline-PRISM's.

## Versioning Scheme

Glimpse uses CalVer `YY.feature.patch`:

- **YY**: two-digit year (`26` for 2026)
- **feature**: incremented for each release containing new features
- **patch**: incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second feature release).
The version is bumped only at release time, not during development. Releases are tagged `v{version}`.

## Files

One file per release, `RELEASE_NOTES_v{version}.md`. The unreleased draft is `RELEASE_NOTES_next.md`,
renamed at release time (so the target version can change while it is a draft, e.g. a planned patch that
becomes a feature release).

## Content

```markdown
# Skyline Glimpse v{version} Release Notes

One-sentence summary of the release.

## New Features
## Bug Fixes
## Performance
## Breaking Changes
```

Omit empty sections. Past tense, lead with what the user sees, give concrete numbers ("the document loads
in 12 s instead of 45 s on a 620,000-transition document").
