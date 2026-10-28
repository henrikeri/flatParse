# FlatMaster v1.0.15

- Fixes **"Error scanning: Unsupported or misplaced output manifest"**, which aborted the whole flat/dark scan.
- Cause: output manifests record the folder they were written to. If the output tree is later moved or copied (for example to a new master-flat location), the recorded folder no longer matches, and the strict check threw an exception that was not handled by the scan.
- Manifests recorded for a different output location are now treated as relocated: the master in the current output folder is reused and shown as completed (inputs are not re-verified), unless its calibration settings differ or the master is missing.
- An unreadable or unsupported manifest now marks only that group as failed with a reason instead of stopping the scan.

Validation: 156 automated tests pass, including new tests for relocated manifests (with and without the state file) and unsupported manifests.

Windows x64 single-file executable; requires the .NET 8 Desktop Runtime.
