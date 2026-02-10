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
