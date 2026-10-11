# FlatMaster v1.0.6

Built: 2026-10-11

## Calibration master imports

- Import only existing master darks and dark-flats chosen by matching selected flat groups with at least three frames.
- Leave unused catalog masters out of the output tree and copy shared matches only once.
- Honor manual dark overrides when choosing which masters to import.
- Import required existing masters even when the same batch also builds masters from raw darks.
- Apply the same selected-job and valid-group filtering to raw-dark materialization previews and builds.

## Distribution

- `FlatMaster-v1.0.6.exe`: framework-dependent single-file Windows x64 executable.
- Requires the .NET 8 Desktop Runtime; the runtime is not bundled.
- Includes the default configuration and managed application dependencies.

## Validation

- 99 automated tests, including regression coverage for matched-only imports, shared masters, manual overrides, skipped groups, and mixed import/integration batches.
