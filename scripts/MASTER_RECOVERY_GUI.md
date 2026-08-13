# Existing-master recovery GUI

`master_recovery_gui.py` finds master images that already exist inside folders
which would otherwise be treated as unprocessed, and copies selected candidates
into a parsed output tree without flattening the directory structure.

## Run

Double-click `run_master_recovery_gui.bat`, or run:

```powershell
python .\scripts\master_recovery_gui.py
```

Optional source and destination defaults can be supplied on the command line:

```powershell
python .\scripts\master_recovery_gui.py "D:\unprocessed flats" --destination "C:\temp\processed_flat"
```

The **Compare two folders…** button opens the exact folder-tree comparison
window described below.

## Selection rules

The scan examines `.fit`, `.fits`, and `.xisf` files. It reads headers only and
does not read image pixels.

A file is shown as a candidate when either of these rules applies:

- **CONFIRMED**: the image has `master` in its filename, or its authoritative
  `IMAGETYP`, `IMAGETYPE`, `FRAMETYPE`, or `FRAME` metadata contains `master`.
  This applies regardless of how many images share the directory, so WBPP-style
  project folders containing many master flats and master darks are handled.
  Confirmed rows missing from the parsed destination are selected by default.
- **REVIEW**: the folder has one image but neither its filename nor image-type
  metadata says it is a master. These rows are unselected by default.
- Non-master images in folders containing multiple images are omitted to avoid
  flooding the review list with raw subframes.

The fast default does not open master-named files and reads metadata only for
single-image folders. This avoids triggering antivirus reads of every huge raw
frame. Enable **Deep metadata scan (slow)** only when you must detect a master
whose filename lacks `master` inside a multi-image directory; that mode opens
every otherwise-unidentified image header.

The **Parsed status** column compares each candidate to the same relative file
path under the selected parsed destination. `MISSING` confirmed rows are selected;
`EXISTS_SAME_SIZE` rows are present and unselected; size conflicts and destination
access errors are highlighted for manual action.

Double-click a row or select rows and press Space to toggle them. Review the
table before pressing **Copy selected**.

## Copy safety

- Each file is copied to the destination beneath the same path it had relative
  to its selected source root.
- Source roots may not overlap one another or the parsed destination.
- Existing destination files are never overwritten.
- If two selections map to the same destination, neither is copied and both are
  reported as collisions.
- A file is copied to a temporary sibling, size-checked, and then published.
- Every copy run writes `_master_recovery_copy_report_*.csv` in the parsed root.
  It includes confirmed, manually selected, unselected, skipped, collided, and
  failed candidates, providing a complete record of the review.

## Hash comparison

Choose two separate, non-overlapping folders and press **Compare folders**.
Files are matched by their relative paths beneath the two selected roots.

- Files present on only one side are reported as `LEFT_ONLY` or `RIGHT_ONLY`.
- Different sizes are reported as `SIZE_DIFFERENT` without wasting time hashing.
- Every equal-sized pair is read completely and hashed. Equal hashes are
  `MATCH`; unequal hashes are `CONTENT_DIFFERENT`.
- SHA-256 is the default. BLAKE2b is also available.
- By default every file is compared. **Only FIT/FITS/XISF images** limits the
  inventory to FlatMaster-supported image formats.
- **Show differences only** keeps a large matching tree readable; clearing it
  shows exact matches too.
- **Export complete CSV…** always writes every comparison row and any inventory
  warnings, regardless of the display filter.

### Network interruptions and resume

Transient Windows/SMB failures such as **The network path was not found** are
retried automatically four times with short 1, 2, and 4 second delays.

If the share remains unavailable during hashing, the current row becomes
`ERROR` and the remaining queue becomes `PENDING_RETRY`. The comparator stops
touching subsequent files, preserving the work already completed instead of
waiting repeatedly on every path.

To continue later:

1. Export the complete CSV if it has not already been exported.
2. Open **Compare two folders…** again.
3. Press **Import complete CSV…** and choose the export.
4. Restore the network connection, then press **Retry / resume**.

Resume inventories folder names and sizes again so paths missed during the
outage can be discovered. Completed hashes are reused when the relative path,
size, and recorded modification time are unchanged; only unresolved, new, or
changed equal-sized pairs are reread. CSVs exported by the earlier version are
also accepted. Because those files did not record modification times, their
completed hashes are reused when paths and sizes match, and the result includes
an explicit warning about that limitation.

Hash comparison is exact and therefore reads every byte of every equal-sized
file pair. It runs outside the UI thread and can be cancelled between 16 MiB
read blocks.

## Tests

```powershell
python -m unittest discover -s .\scripts\tests -v
```
