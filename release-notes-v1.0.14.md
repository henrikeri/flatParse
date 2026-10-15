# FlatMaster v1.0.14

- Adds **Combine compatible flat exposures after separate calibration** in Configuration, off by default and saved between sessions.
- After enabling, scan flats again and run matching. Compatible exposure groups within one folder become one integration group when their combined total reaches three frames.
- Filter, binning, gain, offset, dimensions, and channel count must match. Temperature and dark matching remain specific to each original exposure group.
- Native and PixInsight both calibrate each original exposure separately, then perform multiplicative normalisation and integrate the pooled calibrated frames.
- Missing required calibration for any exposure fails the combined master. Signal-quality rejection and the three-frame minimum apply to the pooled result.
- Matching displays combined exposures and exposure-specific matching reasons. Combined groups use automatic matching per exposure instead of applying a single manual dark override to all frames.
- Underfilled groups absorbed into a combined integration are no longer copied as preservation candidates. Groups with fewer than three frames in total retain the existing preservation behaviour.
- Mixed masters have distinct `Mixed20-30s`-style names and output identities. Their manifests record the original exposure list, complete source inventory, and integrated frame count. Existing separate masters or preserved copies do not count as a completed combined master.
- Includes the quick verification tab, double-click folder opening, and preserved-copy recognition from the local v1.0.12–v1.0.13 builds.

Validation: 153 automated tests pass, including numerical calibration, compatible grouping, provenance, exposure-specific matching, and required calibration-master imports. All 26 focused selection/matching checks pass. Executable checks cover the PixInsight combined pipeline and both UNC-directory helpers. A live synthetic PixInsight run separately calibrated one 20s and two 30s flats, integrated all three, and recorded completed provenance.

Windows x64 single-file executable; requires the .NET 8 Desktop Runtime.
