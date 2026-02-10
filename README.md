# 🔭 FlatMaster - Astronomical Flat Calibration Orchestrator

A professional-grade C# application for processing and integrating astronomical flat calibration frames with intelligent dark frame matching and PixInsight integration.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WPF](https://img.shields.io/badge/UI-WPF-0078D4?logo=windows)
![PixInsight](https://img.shields.io/badge/PixInsight-1.8.9%2B-00A1F1)
![License](https://img.shields.io/badge/license-MIT-green)

## ✨ Features

### 🔍 **Smart Discovery**
- Recursive directory scanning for flat and dark frames
- Automatic exposure grouping (minimum 3 frames per group)
- FITS and XISF format support
- Metadata extraction from headers and filenames

### 🎯 **Intelligent Dark Matching**
- Multi-criteria matching: exposure, binning, gain, offset, temperature
- Automatic master dark generation when needed
- Nearest-exposure fallback with dark optimization
- Configurable matching preferences

### ⚙️ **PixInsight Integration**
- Dynamic PJSR script generation
- WBPP-compatible rejection algorithms (Percentile, Winsorized Sigma, Linear Fit)
- Automatic ImageCalibration and ImageIntegration
- Proper FITS keyword handling

### 🎨 **Modern UI**
- Clean, intuitive WPF interface
- Real-time progress tracking
- Hierarchical dark inventory
- Interactive selection management
- Live processing log

### 📊 **Professional Output**
- Mirrored directory structure (`{BaseDir}_processed/`)
- Consistent naming: `MasterFlat_{DATE}_{FILTER}_{EXPOSURE}s.xisf`
- Optional calibrated intermediate cleanup
- Session logging to file

## 📋 Requirements

- **.NET 8.0 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Windows 10/11** (WPF requirement)
- **PixInsight 1.8.9+** ([Official Site](https://pixinsight.com))

## 🚀 Quick Start

### Using PowerShell Build Script (Recommended)

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

## 📖 Documentation

- **[USAGE.md](USAGE.md)** - Complete usage guide and workflow
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Design decisions and patterns
- **[Contributing](CONTRIBUTING.md)** - How to contribute (coming soon)

## 🏗️ Architecture

```
FlatMaster/
├── src/
│   ├── FlatMaster.Core/              # 🔷 Domain Layer
│   │   ├── Models/                   #    Business entities
│   │   └── Interfaces/               #    Service contracts
│   ├── FlatMaster.Infrastructure/    # 🔧 Infrastructure Layer
│   │   └── Services/                 #    File I/O, PixInsight
│   └── FlatMaster.WPF/               # 🎨 Presentation Layer
│       ├── ViewModels/               #    MVVM ViewModels
│       ├── Views/                    #    XAML UI
│       └── Styles/                   #    Modern styling
└── tests/
    └── FlatMaster.Tests/             # ✅ Test Suite
```

### Design Principles

- ✅ **MVVM Pattern** - Clean separation, testable ViewModels
- ✅ **Dependency Injection** - Loose coupling, easy testing
- ✅ **Async/Await** - Non-blocking UI, responsive experience
- ✅ **SOLID Principles** - Maintainable, extensible code
- ✅ **Nullable Reference Types** - Compile-time null safety

## 🎯 Usage Example

1. **Add directories**: Select flat base folders and dark library roots
2. **Scan**: Discover frames and group by exposure
3. **Review**: Check found directories and dark inventory
4. **Select**: Choose what to process (or use "Select All")
5. **Process**: Click "Process Selected" and monitor progress
6. **Results**: Master flats saved in `{BaseDir}_processed/`

## 🔧 Configuration

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

## 🧪 Testing

Comprehensive test coverage using:
- **xUnit** - Test framework
- **FluentAssertions** - Expressive assertions
- **Moq** - Mocking framework

```bash
dotnet test --verbosity normal
```

## 🛠️ Technology Stack

| Layer | Technology |
|-------|-----------|
| **Framework** | .NET 8.0 (LTS) |
| **UI** | WPF (Windows Presentation Foundation) |
| **MVVM** | CommunityToolkit.Mvvm |
| **DI** | Microsoft.Extensions.DependencyInjection |
| **Logging** | Microsoft.Extensions.Logging |
| **Testing** | xUnit, FluentAssertions, Moq |

## 🤝 Contributing

Contributions welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

### Areas for Contribution
- 🌐 Additional image format support (e.g., TIFF, RAW)
- 📊 Statistics and quality metrics
- 🎨 UI/UX improvements
- 📝 Documentation enhancements
- 🧪 Additional test coverage

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- **PixInsight** team for their excellent astrophotography platform
- **FITS** and **XISF** format specifications
- **.NET Community** for amazing OSS tools

## 📞 Support

- 🐛 **Issues**: [GitHub Issues](https://github.com/yourusername/flatmaster/issues)
- 💬 **Discussions**: [GitHub Discussions](https://github.com/yourusername/flatmaster/discussions)
- 📧 **Email**: your.email@example.com

---

**Built with ❤️ for the astrophotography community**
