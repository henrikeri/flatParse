# 🚀 Quick Start Guide - FlatMaster

Get up and running with FlatMaster in 5 minutes!

## ⚡ Installation

### 1. Prerequisites Check

**Windows 10/11:**
```powershell
# Check Windows version
winver
```

**.NET 8 SDK:**
```powershell
# Check if installed
dotnet --version

# If not, download from:
# https://dotnet.microsoft.com/download/dotnet/8.0
```

**PixInsight:**
- Verify installation path: `C:\Program Files\PixInsight\bin\PixInsight.exe`
- Or note your custom path for later

### 2. Build the Application

```powershell
# Navigate to the project directory
cd C:\Users\riise\Documents\flatParse

# Build and run
.\build.ps1 -Run
```

That's it! The application will open.

## 🎯 First Use - Process Your Flats

### Step 1: Configure PixInsight Path
- If PixInsight is not at the default location, click **"..."** next to PixInsight Executable
- Browse to your `PixInsight.exe`

### Step 2: Add Your Flat Directories
1. Click **"+ Add"** under "Flat Base Directories"
2. Select a folder containing your flat frames (it will scan recursively)
3. Example: `E:\Astrophotography\2024\Session_Jan15\`

### Step 3: Add Your Dark Library
1. Click **"+ Add"** under "Dark Library Directories"
2. Select your dark frames folder
3. Example: `E:\Calibration\DarkLibrary\`

### Step 4: Scan for Frames
1. Click **"🔍 Scan Flat Directories"**
   - Wait for scan to complete
   - Review discovered directories in the right panel
   
2. Click **"🔍 Scan Dark Library"**
   - Wait for scan to complete
   - Review dark inventory (organized by type and exposure)

### Step 5: Review and Select
- **Flat Directories**: Check/uncheck directories you want to process
- **Dark Inventory**: Ensure required exposures are checked (usually all)
- Use **"✓ All"** / **"✗ None"** buttons for quick selection

### Step 6: Process!
1. Click **"▶️ Process Selected"**
2. Monitor progress in the log window
3. Wait for "Processing complete" message

### Step 7: Find Your Master Flats
Your master flats are saved in:
```
{YourFlatBaseDirectory}_processed/
└── {RelativeDirectory}/
    └── MasterFlat_{DATE}_{FILTER}_{EXPOSURE}s.xisf
```

Example:
```
E:\Astrophotography\2024\Session_Jan15_processed\
└── Ha\
    └── MasterFlat_2024-01-15_HA_1.5s.xisf
```

## 📊 Example Directory Structure

### Input (Before):
```
E:\Astrophotography\2024\Session_Jan15\
├── Ha\
│   ├── Flat_Ha_1.5s_001.fits
│   ├── Flat_Ha_1.5s_002.fits
│   ├── Flat_Ha_1.5s_003.fits
│   └── ...
└── OIII\
    ├── Flat_OIII_2s_001.fits
    └── ...

E:\Calibration\DarkLibrary\
├── Darks_1.5s\
│   ├── Dark_1.5s_001.fits
│   └── ...
└── DarkFlats_1.5s\
    └── ...
```

### Output (After):
```
E:\Astrophotography\2024\Session_Jan15_processed\
├── Ha\
│   └── MasterFlat_2024-01-15_HA_1.5s.xisf
└── OIII\
    └── MasterFlat_2024-01-15_OIII_2s.xisf
```

## 🔍 Troubleshooting

### ❌ "No suitable dark found for exposure X.Xs"

**Solution:**
1. Check dark inventory has that exposure
2. Verify darks are checked (selected)
3. Enable "Allow nearest exposure with optimize" in config
4. Add more darks to your library

### ❌ "Skipping directory: no exposure groups with >=3 files"

**Solution:**
- Each exposure needs at least 3 flats
- Check if files are named with exposure (e.g., `1.5s`)
- Files should have EXPTIME in FITS header

### ❌ "PixInsight executable not found"

**Solution:**
1. Verify path in Settings
2. Click "..." to browse
3. Default path: `C:\Program Files\PixInsight\bin\PixInsight.exe`

### ❌ Build errors

**Solution:**
```powershell
# Clean and rebuild
.\build.ps1 -Clean
dotnet restore
dotnet build
```

## 💡 Pro Tips

### Tip 1: Organize Your Flats
```
Session_Jan15\
├── Ha\          ← Filter-specific folders
├── OIII\
└── SII\
```
FlatMaster will create the same structure in `_processed/`

### Tip 2: Build a Comprehensive Dark Library
```
DarkLibrary\
├── MasterDark_1s_Gain100_Offset10_-10C.xisf
├── MasterDark_1.5s_Gain100_Offset10_-10C.xisf
├── MasterDark_2s_Gain100_Offset10_-10C.xisf
└── ...
```
Cover all your common exposures!

### Tip 3: Session Logs
Every run creates a log file:
- Location: `%TEMP%\FlatMaster_{TIMESTAMP}.log`
- Useful for debugging
- Share when reporting issues

### Tip 4: Batch Processing
1. Select multiple flat directories
2. Check all relevant darks
3. Click Process once - handles everything!

### Tip 5: Naming Conventions
FlatMaster auto-detects:
- **Exposure**: From FITS header or filename (`1.5s`)
- **Filter**: From filename (`_HA_`, `_Filter_Ha`) or directory name
- **Date**: From FITS header or directory name (`2024-01-15`)

## 📞 Getting Help

### Check Logs
1. Look at the "Processing Log" panel in the UI
2. Check session log file in `%TEMP%\`

### Documentation
- [USAGE.md](USAGE.md) - Detailed usage guide
- [ARCHITECTURE.md](ARCHITECTURE.md) - Technical details
- [README.md](README.md) - Project overview

### Community
- GitHub Issues: Report bugs
- GitHub Discussions: Ask questions
- Email: your.email@example.com

## 🎉 Success!

Once processing completes:
1. ✅ Master flats are in `{BaseDir}_processed/`
2. ✅ Named consistently: `MasterFlat_{DATE}_{FILTER}_{EXP}s.xisf`
3. ✅ Ready to use in your imaging workflow!

Import these master flats into WBPP, or use directly with ImageCalibration!

---

**Happy flat calibrating! 🔭✨**
