# FlatMaster v1.0.12

- Adds a standalone **Verify masters** tab with separate source and master-root selectors.
- Walks source folders and checks matching relative output folders for non-empty master flats. Uses directory entries, filenames, and file sizes; no processing scan, image metadata reads, or hashing.
- Shows source-image counts, master counts, master paths, and details per folder. Missing and failed folders appear first.
- Recognises FITS/FIT/XISF masters named `MasterFlat`, `Master Flat`, or `master_flat`, case-insensitively. Temporary files and master darks do not count.
- Flags empty masters, inaccessible folders, and linked directories. Excludes FlatMaster's working folders and generated source masters from unprocessed-image counts.
- Supports cancellation and CSV report export. Changing either selected root clears the previous result.
- Double-click a result row to open the matching source and master folders in Explorer. Missing folders open their nearest existing parent within the selected tree, with an explanatory message.

This is a folder-level presence check. It does not establish coverage of each exposure group or validate image contents, and it does not delete files. Re-run if either tree changes.

Validation: 136 automated tests passed, including locked source/master files proving no image contents are read, matching relative paths, missing folders, empty masters, temporary files, multiple formats, cancellation, and CSV export.

Distribution: `FlatMaster-v1.0.12.exe`, Windows x64 single-file executable requiring the .NET 8 Desktop Runtime.
