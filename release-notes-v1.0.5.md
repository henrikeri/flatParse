# FlatMaster v1.0.5

Released: 2026-08-13

This correctness-focused release improves calibration selection, long-running processing reliability, output verification, and preservation of sparse flat groups.

## Download

- `FlatMaster-v1.0.5.exe`
- Windows x64, framework-dependent single-file executable
- Bundles FlatMaster and its managed dependencies; does not bundle the .NET runtime
- Requires the .NET 8 Desktop Runtime

## Calibration and matching fixes

- Bias and master-bias frames are now discovered consistently from metadata and common filename variants, including names containing spaces or underscores.
- Master bias is available in group overrides and is treated as the correct zero-second calibration source for short flat exposures.
- PixInsight now receives master bias through its dedicated master-bias fields instead of the master-dark slot.
- Dark/bias matching validates exposure, temperature, binning, gain, offset, and image geometry before long-running processing starts.
- Configured temperature tolerances are honored without rejecting dark pools whose spread remains within the selected limit.
- Master-dark and master-bias caches now use stricter calibration identities and manifests, preventing incompatible cached products from being reused.

## Sparse-group preservation

- A one-file exposure group is selected by default and copied unchanged into the mirrored destination tree.
- A two-file exposure group remains unselected by default; selecting it copies both files unchanged.
- Groups below three frames are never sent to PixInsight or the native integration engine.
- Existing single master files and single images with unreadable metadata are also preserved.
- Copies are streamed and published atomically while retaining the source modification time.

## PixInsight and processing reliability

- Removed the fixed 20-minute PixInsight attempt timeout that could start duplicate detached processing runs.
- Tracks and terminates only PixInsight processes started by the active attempt, preserving unrelated existing sessions.
- Added progress-heartbeat reporting and more reliable completion-sentinel handling.
- Added atomic publication of generated masters and safer cleanup of temporary output.
- Added sampled calibrated-flat signal validation to catch implausible results without rereading every pixel row.
- Improved native engine parallelism, cancellation, validation, and processing statistics.

## Verification and recovery tools

- Added the `scripts/master_recovery_gui.py` companion tool for comparing input and processed trees, selecting existing masters, copying recovered files, hash comparison, CSV export/import, and retrying interrupted network scans.
- Improved processing reports so output success/failure and storage totals reflect actual results.

## Packaging and UI

- Version advanced to 1.0.5 in assembly, file, manifest, startup log, and UI footer.
- The displayed version now comes from assembly metadata to prevent future drift.
- Default configuration is embedded in the single executable; an adjacent `appsettings.json` can still override it.
- Automated test suite expanded to 92 tests.
