# Building and Running FlatMaster

## Prerequisites

1. **.NET 8 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
2. **Visual Studio 2022** (recommended) or VS Code with C# extension
3. **Windows 10/11** (WPF requirement)
4. **PixInsight 1.8.9+** (for processing)

## Quick Start

### 1. Restore Dependencies

```powershell
dotnet restore
```

### 2. Build Solution

```powershell
dotnet build
```

### 3. Run Application

```powershell
dotnet run --project src/FlatMaster.WPF
```

Or open `FlatMaster.sln` in Visual Studio and press F5.

## Matching existing and newly added flats

Scan the input roots with the intended output folder selected, then run matching.
Matching Details shows every scanned group, with included groups sorted first.
Completed outputs default to unchecked; checking Include explicitly allows reprocessing.

The summary counts completed, included, changed, and failed groups. Use Include
pending to select work needing attention, Clear inclusion to uncheck everything,
or Ctrl/Shift-select rows and choose Include selected rows. Hover over Output
status for details and use Open master to open an available output through its
Windows file association.

New masters store their input inventory and camera settings in an adjacent
`.meta.json` file. Attempt records live in `.FlatMaster` under the mirrored output
directory. Keep these files with the outputs. On the next scan, added, removed,
or modified inputs appear as Changed inputs; interrupted or failed attempts
appear as Failed. Older masters without a recorded inventory remain Completed,
with an explanatory tooltip. Reprocessing them creates provenance for future
comparisons.

Processing retains the full matching list afterward. Successfully completed
groups return to unchecked, while reports describe only the groups in that run.

## Project Structure

```
FlatMaster/
├── src/
│   ├── FlatMaster.Core/              # Domain models, interfaces
│   │   ├── Models/                   # ImageMetadata, DarkFrame, etc.
│   │   └── Interfaces/               # Service contracts
│   ├── FlatMaster.Infrastructure/    # Implementation
│   │   └── Services/                 # Metadata, scanning, PixInsight
│   └── FlatMaster.WPF/               # User interface
│       ├── ViewModels/               # MVVM ViewModels
│       ├── Views/                    # XAML windows
│       ├── Styles/                   # UI styling
│       └── Converters/               # Value converters
└── tests/
    └── FlatMaster.Tests/             # Unit tests
```

## Configuration

Edit `appsettings.json` in the WPF project:

```json
{
  "AppSettings": {
    "PixInsightExecutable": "C:\\Program Files\\PixInsight\\bin\\PixInsight.exe",
    "MaxThreads": 8
  },
  "ProcessingDefaults": {
    "DeleteCalibratedFlats": true,
    "RejectionLowSigma": 5.0,
    "RejectionHighSigma": 5.0
  },
  "DarkMatching": {
    "EnforceBinning": true,
    "PreferSameGainOffset": true,
    "MaxTempDeltaC": 5.0
  }
}
```

## Usage Workflow

### 1. Add Flat Base Directories
- Click **"+ Add"** under "Flat Base Directories"
- Select folders containing flat frames (will scan recursively)

### 2. Add Dark Library Directories
- Click **"+ Add"** under "Dark Library Directories"
- Select folders containing dark/dark-flat frames

### 3. Scan Files
- Click **"🔍 Scan Flat Directories"** to discover flat frames
- Click **"🔍 Scan Dark Library"** to catalog darks
- Review discovered files in the right panels

### 4. Select What to Process
- Check/uncheck directories and darks as needed
- Use "✓ All" and "✗ None" buttons for batch selection

### 5. Process
- Configure PixInsight path if needed
- Click **"▶️ Process Selected"**
- Monitor progress in the log window

### 6. Results
- Master flats saved in `{BaseDir}_processed/` with mirrored structure
- Format: `MasterFlat_{DATE}_{FILTER}_{EXPOSURE}s.xisf`
- Calibrated intermediates deleted (if option checked)

## Running Tests

```powershell
dotnet test
```

## Troubleshooting

### "PixInsight executable not found"
- Verify path in Settings
- Default: `C:\Program Files\PixInsight\bin\PixInsight.exe`

### "No suitable dark found"
- Check dark inventory has matching exposures
- Enable "Allow nearest exposure with optimize" in config
- Verify dark files are selected (checked)

### "Skipping directory: no exposure groups with >=3 files"
- Each exposure needs at least 3 flats for integration
- Check filename patterns if exposure not detected
- Exposures detected from FITS headers or filenames (e.g., `flat_1.5s.fits`)

### Metadata not reading correctly
- Ensure files are valid FITS/XISF format
- Check FITS keywords: EXPTIME, XBINNING, GAIN, OFFSET
- XISF properties/FITSKeywords parsed from XML header

## Performance Tips

1. **Parallel Processing**: Set `MaxThreads` in config (default: 8)
2. **Network Drives**: Copy files locally for faster scanning
3. **Large Libraries**: Use specific dark roots instead of top-level
4. **Filters**: Organize flats by filter in separate folders

## Architecture Notes

### Design Patterns
- **MVVM**: Clean separation of UI and logic
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection
- **Repository Pattern**: Abstracted file operations
- **Async/Await**: Non-blocking UI during operations

### Key Services
- `IMetadataReaderService`: FITS/XISF header parsing
- `IFileScannerService`: Recursive directory scanning
- `IDarkMatchingService`: Intelligent dark frame selection
- `IPixInsightService`: PJSR generation and execution

### PixInsight Integration
- Generates PJSR (JavaScript) scripts dynamically
- Configures ImageIntegration with WBPP-like parameters
- Handles ImageCalibration with dark optimization
- Robust file saving with fallback strategies

## Extending

### Adding New Metadata Fields
1. Update `ImageMetadata` in Core
2. Add parsing logic in `MetadataReaderService`
3. Update `MatchingCriteria` if needed for dark matching

### Custom Processing Options
1. Modify `ProcessingConfiguration` in Core
2. Update `appsettings.json` schema
3. Pass through to PJSR template in `PixInsightService`

### Alternative UI
- Core/Infrastructure layers are UI-agnostic
- Could build Console app, Blazor, or API using same services
- ViewModels use CommunityToolkit.Mvvm (portable)

## License

GNU GPLv3 - See LICENSE file. Third-party notices are listed in THIRD-PARTY-NOTICES.

## Verify masters before removing source images

The **Verify masters** tab is independent of scanning, matching, and processing. Select an unprocessed source root and the corresponding master output root with the same relative folder structure, then click **Compare folders**. For example, source `session/Ha` is compared with output `session/Ha`.

The comparison walks source folders and lists image files without opening them. Each source folder containing FIT/FITS/XISF images gets a row showing the source count, non-empty master count, matching master paths, and status. Missing and failed folders appear first. Master names beginning with `MasterFlat_`, `Master Flat `, or `master_flat_` are recognised case-insensitively; temporary files and master darks do not count. FlatMaster working directories and generated source masters are excluded.

When no master is present, **Copies present** means every source image has a non-empty output copy with the same filename and size. This covers underfilled exposure groups preserved without integration. A folder may contain three or four files split into multiple one- or two-frame groups. Partial copies or copies with different sizes remain missing; copy contents are not read.

An empty master or an inaccessible/linked directory prevents an all-present result. Empty source trees are reported as nothing checked. **Cancel** stops the walk; **Export report** saves a CSV including both roots, check time, and the scope of the comparison. Changing either root clears the previous result. Double-click a result row to open both folders in Explorer; a missing folder opens its nearest existing parent within the selected tree.

This verifies **master presence per folder**, without the full metadata scan. It does not check every exposure group, source-frame coverage, or image integrity, and does not delete files. If a folder contains multiple exposure groups, review its master list before removing source frames. Compare again whenever either tree changes.

## Combine flat exposures after separate calibration

Enable **Combine compatible flat exposures after separate calibration** in Configuration, then scan flats again and run matching. Changing the option clears the previous flat scan and matching choices. The option is off by default and is saved with your settings.

Within each folder, flats sharing filter, binning, gain, offset, image dimensions, and channel count can be integrated together across exposure times. The total must reach three frames. Each original exposure group retains its temperature and its own automatic dark/bias match. The engine calibrates those groups separately, pools the calibrated frames, rejects low-signal frames, normalises brightness, and integrates one master. If a required dark is missing for any exposure, the combined master fails. The final usable pool must still contain at least three frames.

Matching shows labels such as **20 + 30 s (combined)** and details of the separate dark matches. A single manual dark override is unavailable for a combined group. One Include checkbox controls the complete combined integration.

Outputs use distinct names such as `MasterFlat_2026-09-25_HA_Mixed20-30s.xisf`, with the exposure list and full input inventory in the provenance sidecar. Existing separate masters and preserved copies do not satisfy a combined group, so enabling this mode can suggest new combined masters even for previously completed folders. Frames absorbed into a combined group are not copied as underfilled preservation candidates. Folders with fewer than three compatible frames in total keep the previous preservation behaviour.
