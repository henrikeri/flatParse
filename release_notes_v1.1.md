FlatMaster v1.1 — Release Notes

Summary
- Added new processing options for darks and flats and UI exposure.

What’s new
- `RequireDarks`: Option to require dark frames. If darks are missing the image will be gracefully skipped and the event logged as a failed calibration.
- `AllowProcessingWithoutFlats`: Option to allow processing when flats are missing; processing will continue and be tagged in the log.
- UI: New checkboxes in the main settings window to toggle the above options. Version shown in UI bumped to 1.1.
- Configuration: `appsettings.json` updated with new processing defaults.

Build & Distribution
- Artifact: Framework-dependent single-file executable for Windows x64 that bundles internal DLLs (but does NOT include .NET runtime libraries).
- File: `FlatMaster.WPF.exe`
- File size: 162,486,252 bytes (published single-file for win-x64)
- Note: The executable requires a compatible .NET runtime (net8.0-windows) installed on the target machine.

Assets included
- `FlatMaster.WPF.exe` (single-file bundle of app + internal DLLs)

Notes & Next steps
- If you want a ZIP of the entire `publish/` folder, a signed checksum (SHA256), or additional supporting DLLs attached, tell me and I'll add them to the release.
- If you need installation instructions or an installer package, I can prepare those as well.

Changelog
- Backend: Added `RequireDarks` and `AllowProcessingWithoutFlats` flags to processing configuration and models.
- UI: Exposed options in `MainWindow.xaml` and `MainViewModel.cs` and updated version text to `v1.1`.
- Misc: Minor build fixes and publishing artifacts prepared.

Released by: CI / manual publish on 2026-02-20
