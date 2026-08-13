from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIRECTORY))

import master_recovery  # noqa: E402
from master_recovery import (  # noqa: E402
    Candidate,
    compare_folders,
    copy_candidates,
    read_comparison_report,
    scan_source_roots,
    write_comparison_report,
    write_copy_report,
)


def fits_card(key: str, value: str | None = None) -> bytes:
    if value is None:
        text = key
    else:
        text = f"{key:<8}= {value}"
    return text.ljust(80).encode("ascii")


def write_fits(path: Path, image_type: str = "FLAT") -> None:
    cards = [
        fits_card("SIMPLE", "T"),
        fits_card("BITPIX", "16"),
        fits_card("NAXIS", "0"),
        fits_card("IMAGETYP", f"'{image_type}'"),
        fits_card("END"),
    ]
    header = b"".join(cards)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header.ljust(2880, b" "))


def write_xisf(path: Path, image_type: str = "Flat") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        (
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<xisf version="1.0"><Image geometry="1:1:1">'
            f'<FITSKeyword name="IMAGETYP" value="\'{image_type}\'" />'
            "</Image></xisf>"
        ).encode("utf-8")
        + b"\0\0\0\0"
    )


class ScanTests(unittest.TestCase):
    def test_master_in_filename_is_selected_by_default(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "MasterFlat_R.fit", "FLAT")

            result = scan_source_roots([source], parsed_root=parsed)

            self.assertEqual(1, len(result.candidates))
            candidate = result.candidates[0]
            self.assertTrue(candidate.selected_by_default)
            self.assertEqual("CONFIRMED", candidate.confidence)
            self.assertIn("filename", candidate.reason)

    def test_fits_master_metadata_is_selected_by_default(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "integration.fit", "Master Flat")

            candidate = scan_source_roots([source], parsed_root=parsed).candidates[0]

            self.assertTrue(candidate.selected_by_default)
            self.assertIn("metadata", candidate.reason)
            self.assertIn("IMAGETYP=Master Flat", candidate.reason)

    def test_xisf_master_metadata_is_selected_by_default(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_xisf(source / "night" / "integration.xisf", "Master Flat")

            candidate = scan_source_roots([source], parsed_root=parsed).candidates[0]

            self.assertTrue(candidate.selected_by_default)
            self.assertIn("metadata", candidate.reason)

    def test_unidentified_single_image_is_unselected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "flat.fit", "FLAT")

            candidate = scan_source_roots([source], parsed_root=parsed).candidates[0]

            self.assertFalse(candidate.selected_by_default)
            self.assertEqual("REVIEW", candidate.confidence)

    def test_master_in_multi_image_directory_is_candidate_but_raw_is_not(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "MasterFlat_R.fit", "Master Flat")
            write_fits(source / "night" / "flat_2.fit", "FLAT")

            result = scan_source_roots([source], parsed_root=parsed)

            self.assertEqual(1, len(result.candidates))
            self.assertEqual("MasterFlat_R.fit", result.candidates[0].image_path.name)
            self.assertTrue(result.candidates[0].selected_by_default)
            self.assertEqual(2, result.image_files_seen)

    def test_all_rosette_style_masters_in_one_directory_are_found(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "Esprit"
            parsed = Path(temporary) / "Esprit Master Flats"
            rosette = source / "Rosette Project"
            for index in range(1, 13):
                write_xisf(
                    rosette / f"masterFlat_FILTER-Ha_TAKE-{index}.xisf",
                    "Master Flat",
                )
            for index in range(1, 14):
                write_xisf(
                    rosette / f"masterDark_EXPOSURE-{index}.xisf",
                    "Master Dark",
                )

            result = scan_source_roots([source], parsed_root=parsed)

            self.assertEqual(25, len(result.candidates))
            self.assertTrue(all(item.selected_by_default for item in result.candidates))
            self.assertTrue(all(item.destination_status == "MISSING" for item in result.candidates))

    def test_metadata_master_in_multi_image_directory_is_found(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "integration.fit", "Master Flat")
            write_fits(source / "night" / "raw.fit", "FLAT")

            result = scan_source_roots(
                [source], parsed_root=parsed, deep_metadata_scan=True
            )

            self.assertEqual(1, len(result.candidates))
            self.assertEqual("integration.fit", result.candidates[0].image_path.name)
            self.assertIn("metadata", result.candidates[0].reason)

    def test_default_scan_does_not_open_multi_image_raws(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            write_fits(source / "night" / "MasterFlat_R.fit", "Master Flat")
            write_fits(source / "night" / "raw.fit", "FLAT")

            with patch.object(
                master_recovery,
                "read_master_metadata_hint",
                side_effect=AssertionError("fast scan should not open either multi-image file"),
            ):
                result = scan_source_roots([source], parsed_root=parsed)

            self.assertEqual(1, len(result.candidates))
            self.assertEqual("MasterFlat_R.fit", result.candidates[0].image_path.name)

    def test_existing_destination_is_reported_and_not_selected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            parsed = Path(temporary) / "parsed"
            source_file = source / "night" / "MasterFlat_R.fit"
            destination_file = parsed / "night" / "MasterFlat_R.fit"
            write_fits(source_file, "Master Flat")
            write_fits(destination_file, "Master Flat")

            candidate = scan_source_roots([source], parsed_root=parsed).candidates[0]

            self.assertEqual("EXISTS_SAME_SIZE", candidate.destination_status)
            self.assertFalse(candidate.selected_by_default)
            self.assertEqual("CONFIRMED", candidate.confidence)


class CopyTests(unittest.TestCase):
    def test_copy_preserves_relative_folder_and_never_overwrites(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            source = base / "source"
            parsed = base / "parsed"
            image = source / "target" / "date" / "MasterFlat_R.fit"
            write_fits(image, "Master Flat")
            candidate = scan_source_roots([source], parsed_root=parsed).candidates[0]

            first = copy_candidates([candidate], parsed)
            destination = parsed / "target" / "date" / image.name
            self.assertEqual("COPIED", first[0].status)
            self.assertEqual(image.read_bytes(), destination.read_bytes())

            second = copy_candidates([candidate], parsed)
            self.assertEqual("EXISTS_SAME_SIZE", second[0].status)
            self.assertEqual(image.read_bytes(), destination.read_bytes())

            report = write_copy_report(first + second, parsed)
            self.assertTrue(report.exists())
            self.assertIn("COPIED", report.read_text(encoding="utf-8-sig"))

            full_report = write_copy_report(
                first,
                parsed,
                candidates=[candidate],
                selected_keys=set(),
            )
            report_text = full_report.read_text(encoding="utf-8-sig")
            self.assertIn("NOT_SELECTED", report_text)
            self.assertIn("CONFIRMED", report_text)

    def test_destination_collision_copies_neither_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            first_root = base / "first"
            second_root = base / "second"
            parsed = base / "parsed"
            first_image = first_root / "same" / "MasterFlat.fit"
            second_image = second_root / "same" / "MasterFlat.fit"
            write_fits(first_image, "Master Flat")
            write_fits(second_image, "Master Flat")
            candidates = [
                scan_source_roots([first_root], parsed_root=parsed).candidates[0],
                scan_source_roots([second_root], parsed_root=parsed).candidates[0],
            ]

            results = copy_candidates(candidates, parsed)

            self.assertEqual(["COLLISION", "COLLISION"], [item.status for item in results])
            self.assertFalse((parsed / "same" / "MasterFlat.fit").exists())


class FolderComparisonTests(unittest.TestCase):
    def test_comparison_reports_matches_differences_and_missing_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            left = base / "left"
            right = base / "right"
            (left / "nested").mkdir(parents=True)
            (right / "nested").mkdir(parents=True)
            (left / "nested" / "match.bin").write_bytes(b"same content")
            (right / "nested" / "match.bin").write_bytes(b"same content")
            (left / "content.bin").write_bytes(b"aaaa")
            (right / "content.bin").write_bytes(b"bbbb")
            (left / "size.bin").write_bytes(b"short")
            (right / "size.bin").write_bytes(b"a longer file")
            (left / "left-only.bin").write_bytes(b"left")
            (right / "right-only.bin").write_bytes(b"right")

            result = compare_folders(left, right, algorithm="sha256")

            statuses = {
                str(item.relative_path).replace("\\", "/"): item.status
                for item in result.comparisons
            }
            self.assertEqual("MATCH", statuses["nested/match.bin"])
            self.assertEqual("CONTENT_DIFFERENT", statuses["content.bin"])
            self.assertEqual("SIZE_DIFFERENT", statuses["size.bin"])
            self.assertEqual("LEFT_ONLY", statuses["left-only.bin"])
            self.assertEqual("RIGHT_ONLY", statuses["right-only.bin"])
            self.assertEqual(1, result.matching_files)
            self.assertEqual(4, result.differing_files)

            report = write_comparison_report(result, base / "comparison.csv")
            report_text = report.read_text(encoding="utf-8-sig")
            self.assertIn("CONTENT_DIFFERENT", report_text)
            self.assertIn("nested\\match.bin", report_text)

            imported = read_comparison_report(report)
            self.assertEqual(left.resolve(), imported.left_root.resolve())
            self.assertEqual(right.resolve(), imported.right_root.resolve())
            self.assertEqual(result.algorithm, imported.algorithm)
            self.assertEqual(
                [item.status for item in result.comparisons],
                [item.status for item in imported.comparisons],
            )

    def test_images_only_filter_ignores_other_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            left = base / "left"
            right = base / "right"
            left.mkdir()
            right.mkdir()
            write_fits(left / "master.fit", "Master Flat")
            write_fits(right / "master.fit", "Master Flat")
            (left / "notes.txt").write_text("left", encoding="utf-8")
            (right / "notes.txt").write_text("right", encoding="utf-8")

            result = compare_folders(left, right, images_only=True)

            self.assertEqual(1, len(result.comparisons))
            self.assertEqual("MATCH", result.comparisons[0].status)
            self.assertEqual(1, result.left_files)
            self.assertEqual(1, result.right_files)

    def test_comparison_rejects_overlapping_folders(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            left = Path(temporary) / "left"
            right = left / "nested"
            right.mkdir(parents=True)

            with self.assertRaisesRegex(ValueError, "may not overlap"):
                compare_folders(left, right)

    def test_resume_reuses_completed_hashes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            left = base / "left"
            right = base / "right"
            left.mkdir()
            right.mkdir()
            (left / "match.bin").write_bytes(b"same")
            (right / "match.bin").write_bytes(b"same")
            first = compare_folders(left, right)

            with patch.object(
                master_recovery,
                "_hash_file",
                side_effect=AssertionError("unchanged files should not be rehashed"),
            ):
                resumed = compare_folders(left, right, previous_result=first)

            self.assertEqual(1, resumed.matching_files)
            self.assertEqual(1, resumed.reused_hash_pairs)
            self.assertEqual(0, resumed.hashed_bytes)
            self.assertIn("Reused completed hashes", resumed.comparisons[0].detail)

    def test_transient_network_error_is_retried(self) -> None:
        attempts = 0

        def flaky_operation() -> str:
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                error = OSError("The network path was not found")
                error.winerror = 53  # type: ignore[attr-defined]
                raise error
            return "ok"

        with patch.object(master_recovery, "_wait_for_retry"):
            result = master_recovery._retry_io(
                flaky_operation,
                description="testing network retry",
                cancel_event=None,
                progress=None,
                attempts=2,
            )

        self.assertEqual("ok", result)
        self.assertEqual(2, attempts)

    def test_old_csv_without_timestamps_can_resume(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            left = base / "left"
            right = base / "right"
            left.mkdir()
            right.mkdir()
            (left / "match.bin").write_bytes(b"same")
            (right / "match.bin").write_bytes(b"same")
            current = compare_folders(left, right)
            item = current.comparisons[0]
            old_report = base / "old-report.csv"
            old_report.write_text(
                "left_root," + str(left) + "\n"
                "right_root," + str(right) + "\n"
                "algorithm,sha256\n"
                "images_only,False\n"
                "inventory_warnings,0\n\n"
                "status,relative_path,left_size,right_size,left_hash,right_hash,left_path,right_path,detail\n"
                f"MATCH,match.bin,4,4,{item.left_hash},{item.right_hash},"
                f"{left / 'match.bin'},{right / 'match.bin'},\n",
                encoding="utf-8",
            )

            imported = read_comparison_report(old_report)
            with patch.object(
                master_recovery,
                "_hash_file",
                side_effect=AssertionError("legacy completed hashes should be reused"),
            ):
                resumed = compare_folders(left, right, previous_result=imported)

            self.assertEqual(1, resumed.reused_hash_pairs)
            self.assertTrue(
                any("older CSV" in warning for warning in resumed.inventory_warnings)
            )

    def test_persistent_network_failure_leaves_remaining_pairs_pending(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            left = base / "left"
            right = base / "right"
            left.mkdir()
            right.mkdir()
            for filename in ("one.bin", "two.bin"):
                (left / filename).write_bytes(b"same")
                (right / filename).write_bytes(b"same")
            network_error = OSError("The network path was not found")
            network_error.winerror = 53  # type: ignore[attr-defined]

            with patch.object(master_recovery, "_hash_file", side_effect=network_error) as hasher:
                result = compare_folders(left, right)

            self.assertEqual(1, hasher.call_count)
            self.assertEqual(
                ["ERROR", "PENDING_RETRY"],
                [item.status for item in result.comparisons],
            )
            self.assertEqual(2, result.unresolved_files)


if __name__ == "__main__":
    unittest.main()
