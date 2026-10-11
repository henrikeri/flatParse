# FlatMaster v1.0.11

Built: 2026-10-11

- Fixes PixInsight directory creation on UNC output shares. Recursive creation could attempt to create a server-only path such as `//tower.local`, producing Win32 error 161 before calibration.
- Creates missing directories one level at a time, preserving the UNC share boundary and existing drive roots.
- Applies the fix to both flat-processing and master-dark templates.
- Unavailable shares fail with an explicit message instead of attempting to create server or share roots.
- Retains the matching, selection, status, and provenance improvements from v1.0.10.

Validation: 127 automated tests pass. Both templates pass executable directory-helper checks. A live PixInsight run created nested directories on the configured UNC share and removed the isolated test folder. Live synthetic calibration and integration succeeded, and the generated completion receipt was read back into a Completed provenance record.

Distribution: `FlatMaster-v1.0.11.exe`, framework-dependent single-file Windows x64 executable. Requires the .NET 8 Desktop Runtime.
