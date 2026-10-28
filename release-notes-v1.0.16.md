# FlatMaster v1.0.16

- **PixInsight master flats and dark/bias masters are now saved with XISF compression** (`zstd+sh`: Zstandard with byte shuffling, the codec XisfOptimizer also favours for flat masters). On a real 9576x6388 Float64 flat master this took the file from 489 MB to 274 MB in about 2 seconds.
- Why it was not compressing before: the master hints were passed to `ImageWindow.saveAs()`, which has no hints parameter, so the call silently fell through to a plain uncompressed save. The old default hints also used semicolons, which PixInsight does not read. Compressed output is now written through `FileFormatInstance`, which does accept writer hints.
- Sample format, dimensions, colour space and FITS keywords are carried over exactly (verified lossless for Float64, Float32 RGB and UInt16). If a compressed save ever fails, the file is still written uncompressed, so output is never lost.
- Hint strings are normalised, so older `appsettings.json` values that use semicolons also work.
- Default hints: `compression-codec zstd+sh`. To squeeze about 13% more (at roughly 100 times the save time) set `ProcessingDefaults:XisfHintsMaster` to `compression-codec zstd+sh compression-level 100`; an empty value disables compression. The level hint is effectively on/off: values below 100 give the same output as the default.
- Calibrated intermediate flats are still written uncompressed (they are deleted after integration by default).

Validation: 159 automated tests plus the template script tests pass. Live PixInsight 1.9.5 runs exercised both templates' save routine on Float64, Float32 RGB and UInt16 images, re-opened the results and compared samples (identical), and checked headers for compression and keywords.

Windows x64 single-file executable; requires the .NET 8 Desktop Runtime.
