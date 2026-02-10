# FlatMaster — Astronomical Flat Calibration Orchestrator

A C# application for processing and integrating astronomical flat calibration frames with automatic dark frame matching and PixInsight integration.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WPF](https://img.shields.io/badge/UI-WPF-0078D4?logo=windows)
![PixInsight](https://img.shields.io/badge/PixInsight-1.8.9%2B-00A1F1)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

### Smart Discovery
- Recursive directory scanning for flat and dark frames
- Automatic exposure grouping (minimum 3 frames per group)
- FITS and XISF format support
- Metadata extraction from headers and filenames

### Dark Frame Matching
- Multi-criteria matching: exposure, binning, gain, offset, temperature
- Automatic master dark generation when needed
- Nearest-exposure fallback with dark optimization
- Configurable matching preferences

### PixInsight Integration
- Dynamic PJSR script generation
- WBPP-compatible rejection algorithms (Percentile, Winsorized Sigma, Linear Fit)
- Automatic ImageCalibration and ImageIntegration
- Proper FITS keyword handling

### Native Processing Engine
- Built-in calibration and integration (no PixInsight dependency required)
- Exact histogram median normalization
- EqualizeFluxes rejection normalization
- Pixel-level accuracy within Float32 precision of PixInsight output

### User Interface
- WPF interface with real-time progress tracking
- Hierarchical dark inventory view
- Interactive selection management
- Live processing log

### Output
- Mirrored directory structure (`{BaseDir}_processed/`)
- Consistent naming: `MasterFlat_{DATE}_{FILTER}_{EXPOSURE}s.xisf`
- Optional calibrated intermediate cleanup
- Session logging to file

## Requirements

- **.NET 8.0 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Windows 10/11** (WPF requirement)
- **PixInsight 1.8.9+** ([Official Site](https://pixinsight.com)) — only needed when using the PI engine

## Quick Start

### Using PowerShell Build Script

```powershell
# Build and run
.\build.ps1 -Run

# Build, test, and run
.\build.ps1 -Test -Run

# Clean build
.\build.ps1 -Clean -Configuration Release
```

### Manual Build

```bash
# Restore packages
dotnet restore

# Build solution
dotnet build --configuration Release

# Run application
dotnet run --project src/FlatMaster.WPF
```

### Run Tests

```bash
dotnet test
```

## Documentation

- **[USAGE.md](USAGE.md)** — Complete usage guide and workflow
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — Design decisions and patterns

## Architecture

```
FlatMaster/
├── src/
│   ├── FlatMaster.Core/              # Domain Layer
│   │   ├── Models/                   #   Business entities
│   │   └── Interfaces/              #   Service contracts
│   ├── FlatMaster.Infrastructure/    # Infrastructure Layer
│   │   └── Services/                #   File I/O, PixInsight, Native engine
│   └── FlatMaster.WPF/              # Presentation Layer
│       ├── ViewModels/              #   MVVM ViewModels
│       ├── Views/                   #   XAML UI
│       └── Styles/                  #   Styling
└── tests/
    └── FlatMaster.Tests/             # Test Suite
```

### Design Principles

- **MVVM Pattern** — Clean separation, testable ViewModels
- **Dependency Injection** — Loose coupling via `Microsoft.Extensions.DependencyInjection`
- **Async/Await** — Non-blocking UI throughout
- **SOLID Principles** — Maintainable, extensible code
- **Nullable Reference Types** — Compile-time null safety

## Usage

1. **Add directories** — Select flat base folders and dark library roots
2. **Scan** — Discover frames and group by exposure
3. **Review** — Check found directories and dark inventory
4. **Select** — Choose what to process (or use "Select All")
5. **Process** — Click "Process Selected" and monitor progress
6. **Results** — Master flats saved in `{BaseDir}_processed/`

## Configuration

Edit `src/FlatMaster.WPF/appsettings.json`:

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
    "MaxTempDeltaC": 5.0,
    "AllowNearestExposureWithOptimize": true
  }
}
```

## Testing

Test coverage using xUnit, FluentAssertions, and Moq:

```bash
dotnet test --verbosity normal
```

## Technology Stack

| Layer | Technology |
|-------|-----------|
| **Framework** | .NET 8.0 (LTS) |
| **UI** | WPF (Windows Presentation Foundation) |
| **MVVM** | CommunityToolkit.Mvvm |
| **DI** | Microsoft.Extensions.DependencyInjection |
| **Logging** | Microsoft.Extensions.Logging |
| **Testing** | xUnit, FluentAssertions, Moq |

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Links

- [Issues](https://github.com/henrikeri/flatParse/issues)
- [PixInsight](https://pixinsight.com)
