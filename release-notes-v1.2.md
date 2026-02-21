# FlatMaster v1.2

## Highlights
- Added deterministic dark/bias matching priority used by both Native and PixInsight engines.
- Improved PixInsight launch/retry stability and script execution handling.
- Fixed dark tree selection behavior in UI so parent selection propagates to children.
- Published as a single-file framework-dependent Windows executable (`.NET runtime not bundled`).

## Dark/Bias Matching Priority
Flat groups now select calibration data in this exact order:
1. Exact dark match (exposure + metadata scoring for temp/gain/offset/binning)
2. Nearest dark with exposure delta `<= 2s` (no optimize)
3. Nearest dark with exposure delta `> 2s` and `<= 10s` (optimize enabled)
4. Bias fallback (MasterBias first, else build from bias frames when available)
5. No dark/bias found: integrate flats without subtraction when `Require darks` is off

## Additional Fixes
- Fixed flat parsing/scanning reliability regressions from previous iteration.
- Fixed PixInsight "Invalid application instance index"/"Empty script" failure handling.
- Added matching boundary tests for exact `2s` and `10s` deltas.
- Ensured PixInsight template is embedded for robust single-file deployment.
