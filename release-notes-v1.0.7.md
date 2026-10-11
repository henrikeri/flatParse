# FlatMaster v1.0.7

Built: 2026-10-11

## Select only pending flat outputs

- Scan the configured mirrored output tree alongside the input inventory.
- Leave directories with completed output unselected and leave completed groups unchecked during matching.
- Recognize Native and PixInsight master-flat names, including different dates and FIT/FITS/XISF formats.
- Match by mirrored directory, filter, exposure, and binning when encoded in the filename.
- Detect single masters already copied unchanged into the output tree.
- Refresh output detection before matching and processing, so existing masters are excluded even if output appeared after the initial scan.
- Newly added groups without a corresponding output remain eligible for processing.
- Newly discovered directories keep their pending-output selection default instead of inheriting exclusion from an older saved directory list.
- Retain the matched-only calibration-master import fix from v1.0.6.

## Distribution

- `FlatMaster-v1.0.7.exe`: framework-dependent single-file Windows x64 executable.
- Requires the .NET 8 Desktop Runtime.

## Validation

- Regression coverage includes 2,500 groups with 2,400 existing masters, leaving exactly 100 pending groups.
- Tests cover both processing engines' filenames, incorrect filters/binning/directories, changed output inventories, and previously preserved single masters.
