# FlatMaster Architecture (Current)

This repository now has a full HTML5 architecture specification and companion guides:

- `flatmaster_architecture.html`
- `flatmaster_developer_guide.html`
- `flatmaster_logic_guide.html`
- `flatmaster_user_guide.html`

## High-level layout

- `src/FlatMaster.Core`: contracts and data models
- `src/FlatMaster.Infrastructure`: scanner, matching, materialization, native processing, PixInsight integration, reporting
- `src/FlatMaster.WPF`: UI, orchestration, settings persistence, progress/report presentation
- `tests/FlatMaster.Tests`: unit tests for core logic and service behavior

## Runtime pipeline

1. Scan flat and dark roots
2. Build exposure groups and dark catalog
3. Auto-generate matching diagnostics
4. Materialize required master darks
5. Run processing engine (Native or PixInsight)
6. Regenerate diagnostics and render final report

## Matching contract

- Priority waterfall P1 to P8 is implemented in `DarkMatchingService`
- Temperature strict gate and bias-guard thresholds are configurable
- Group-level manual override is supported and persisted through processing plan construction

## Output contract

- Export format is configurable: `XISF` or `FITS`
- Master dark path format:
  - `OutputRoot/Master/Darks/<seconds>s/<temp>degC/MasterDark_<seconds>s_<temp>degC.<ext>`
- Master flat naming differs slightly by engine (see logic guide)

For complete details, use the HTML5 docs listed at the top.
