# FlatMaster v1.0.8

Built: 2026-10-11

- Matching Details now shows groups from every scanned directory, including completed directories unchecked by the output scan.
- Groups with existing output masters remain visible with Include unchecked by default and an Already processed explanation.
- Groups in manually unchecked directories also remain visible with Include unchecked.
- Explicitly checking Include allows reprocessing a completed group, including groups in directories automatically unchecked during scanning.
- Processing honors the Include choices shown in Matching Details.
- Automatic defaults remain separate from explicit user choices, so an output created after matching is still skipped unless the user chose Include.
- Retains the pending-output detection and calibration-master import fixes from v1.0.7.

Distribution: `FlatMaster-v1.0.8.exe`, framework-dependent single-file Windows x64 executable. Requires the .NET 8 Desktop Runtime.
