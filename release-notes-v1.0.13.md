# FlatMaster v1.0.13

- Fixes false missing-master results for folders whose underfilled exposure groups were preserved as source copies.
- Shows **Copies present** when every source image has a non-empty output copy with the same filename and size. No image contents or metadata are read.
- Reports partial or different-size copies as missing, including the matched copy count.
- Keeps **Master found** separate from **Copies present** and includes both in the completion summary and CSV report.
- Includes the independent Verify masters tab and double-click source/output folder opening introduced in the local v1.0.12 build.

The quick check establishes folder-level output presence. It does not verify image contents or per-exposure coverage. A folder containing three or four source images can still consist entirely of one- or two-frame exposure groups.

Validation: 140 automated tests passed. A read-only recheck of the reported NAS trees found 123 source folders: 116 with masters, seven with matching source copies, zero missing and zero failed.
