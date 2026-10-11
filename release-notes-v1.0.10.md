# FlatMaster v1.0.10

Built: 2026-10-11

## Matching and selection

- Matching Details retains every scanned group after processing, including groups outside the current run. Reports still describe only the current run.
- Shows live counts for groups, completed outputs, included groups, changed inputs, and failures.
- Adds Include pending, Clear inclusion, and Include selected rows. Use Ctrl/Shift to select multiple rows.
- Displays Pending, Completed, Changed inputs, or Failed per group, with details in the status tooltip.
- Adds Open master for available output files, using the Windows file association.
- Included groups remain sorted first. Explicit Include choices allow reprocessing completed masters.
- Successful groups return to unchecked after the run; failed and changed groups remain eligible.

## Master provenance

- New Native and PixInsight masters receive a `.meta.json` sidecar containing the source-file inventory, input and integrated frame counts, filter, exposure, gain, offset, binning, geometry, engine, and output file size/modification time.
- The source inventory is captured before processing. Newly added, removed, or modified inputs are detected on the next scan.
- Per-group attempt records under `.FlatMaster` distinguish failed or interrupted attempts from completed output. Failed reprocessing preserves the previous master manifest.
- PixInsight reports completion separately for each group, allowing earlier successful groups to retain provenance if a later group fails.
- Existing masters without provenance remain Completed and unchecked by default; their tooltip explains that the original input inventory is unavailable. Reprocessing creates provenance for future comparisons.
- Automatic directory exclusions are recalculated on each scan. Only explicit user exclusions persist when inputs change. Older settings cannot distinguish automatic from manual exclusions, so pending defaults are reapplied on the first upgraded scan.

## Validation and distribution

- 127 automated tests, including source changes, metadata mismatches, output truncation, missing output, failed attempts, PixInsight completion receipts, and Native integration frame counts.
- Focused Windows UI checks cover retained rows, explicit reprocessing, changed-input defaults, live summary notifications, and selection shortcuts.
- `FlatMaster-v1.0.10.exe`: framework-dependent single-file Windows x64 executable; requires the .NET 8 Desktop Runtime.
