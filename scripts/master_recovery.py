"""Core scanning and copy logic for the FlatMaster recovery GUI.

The scanner deliberately reads image headers only.  A directory is a recovery
candidate when it contains exactly one supported astronomical image:

* the candidate is selected by default when its filename or authoritative
  image-type metadata identifies it as a master;
* otherwise it is presented for manual review and left unselected.

Copying preserves the directory path relative to the source root.  Existing
destination files are never overwritten.
"""

from __future__ import annotations

import csv
import errno
import hashlib
import html
import os
import re
import shutil
import stat
import threading
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Iterable, Sequence


SUPPORTED_EXTENSIONS = frozenset({".fit", ".fits", ".xisf"})
IMAGE_TYPE_KEYS = frozenset(
    {
        "IMAGETYP",
        "IMAGETYPE",
        "IMAGE-TYP",
        "FRAMETYPE",
        "FRAME-TYP",
        "FRAME",
    }
)
MAX_FITS_HEADER_BYTES = 2880 * 100
MAX_XISF_HEADER_BYTES = 16 * 1024 * 1024

_MASTER_RE = re.compile(r"master", re.IGNORECASE)
_XISF_CLOSE_RE = re.compile(rb"</\s*(?:[A-Za-z_][\w.-]*:)?xisf\s*>", re.IGNORECASE)
_XISF_METADATA_TAG_RE = re.compile(
    r"<(?:[A-Za-z_][\w.-]*:)?(?:FITSKeyword|Property)\b[^>]*>",
    re.IGNORECASE | re.DOTALL,
)
_XML_ATTRIBUTE_RE = re.compile(
    r"([A-Za-z_][\w:.-]*)\s*=\s*(?:\"([^\"]*)\"|'([^']*)')",
    re.DOTALL,
)


class ScanCancelled(Exception):
    """Raised internally when a caller cancels a scan."""


@dataclass(frozen=True, slots=True)
class MetadataHint:
    is_master: bool
    detail: str = ""
    warning: str = ""


@dataclass(frozen=True, slots=True)
class Candidate:
    source_root: Path
    folder: Path
    relative_folder: Path
    image_path: Path
    size_bytes: int
    selected_by_default: bool
    confidence: str
    reason: str
    metadata_warning: str = ""
    destination_status: str = "NOT_CHECKED"
    destination_size: int | None = None

    @property
    def key(self) -> str:
        return os.path.normcase(os.path.abspath(self.image_path))

    def destination_path(self, parsed_root: Path) -> Path:
        return parsed_root / self.relative_folder / self.image_path.name


@dataclass(slots=True)
class ScanResult:
    candidates: list[Candidate] = field(default_factory=list)
    directories_scanned: int = 0
    image_files_seen: int = 0
    single_image_directories: int = 0
    warnings: list[str] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class CopyResult:
    source: Path
    destination: Path
    status: str
    size_bytes: int
    detail: str = ""


@dataclass(frozen=True, slots=True)
class FolderComparison:
    relative_path: Path
    left_path: Path | None
    right_path: Path | None
    status: str
    left_size: int | None = None
    right_size: int | None = None
    left_hash: str = ""
    right_hash: str = ""
    detail: str = ""
    left_mtime_ns: int | None = None
    right_mtime_ns: int | None = None


@dataclass(slots=True)
class FolderComparisonResult:
    left_root: Path
    right_root: Path
    algorithm: str
    images_only: bool
    comparisons: list[FolderComparison] = field(default_factory=list)
    left_files: int = 0
    right_files: int = 0
    hashed_bytes: int = 0
    inventory_warnings: list[str] = field(default_factory=list)
    reused_hash_pairs: int = 0

    @property
    def matching_files(self) -> int:
        return sum(item.status == "MATCH" for item in self.comparisons)

    @property
    def differing_files(self) -> int:
        return sum(
            item.status
            in {
                "CONTENT_DIFFERENT",
                "SIZE_DIFFERENT",
                "LEFT_ONLY",
                "RIGHT_ONLY",
            }
            for item in self.comparisons
        )

    @property
    def unresolved_files(self) -> int:
        return sum(item.status in {"ERROR", "PENDING_RETRY"} for item in self.comparisons)


ProgressCallback = Callable[[int, int, str], None]


def _path_key(path: Path) -> str:
    return os.path.normcase(os.path.abspath(path))


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.resolve(strict=False).relative_to(parent.resolve(strict=False))
        return True
    except ValueError:
        return False


def paths_overlap(left: Path, right: Path) -> bool:
    """Return True when either path contains the other (or they are equal)."""

    return _is_relative_to(left, right) or _is_relative_to(right, left)


def validate_roots(source_roots: Sequence[Path], parsed_root: Path | None = None) -> list[str]:
    """Return human-readable validation errors for a scan/copy configuration."""

    errors: list[str] = []
    if not source_roots:
        errors.append("Add at least one source root.")

    seen: set[str] = set()
    for root in source_roots:
        key = _path_key(root)
        if key in seen:
            errors.append(f"Source root is listed more than once: {root}")
        seen.add(key)
        if not root.exists():
            errors.append(f"Source root does not exist: {root}")
        elif not root.is_dir():
            errors.append(f"Source root is not a directory: {root}")
        if parsed_root is not None and paths_overlap(root, parsed_root):
            errors.append(
                f"Source and parsed roots must not overlap: {root} and {parsed_root}"
            )

    for index, root in enumerate(source_roots):
        for other in source_roots[index + 1 :]:
            if paths_overlap(root, other):
                errors.append(f"Source roots must not overlap: {root} and {other}")

    if parsed_root is not None and parsed_root.exists() and not parsed_root.is_dir():
        errors.append(f"Parsed destination is not a directory: {parsed_root}")
    return errors


def _strip_fits_comment(value: str) -> str:
    in_quote = False
    index = 0
    while index < len(value):
        char = value[index]
        if char == "'":
            if in_quote and index + 1 < len(value) and value[index + 1] == "'":
                index += 2
                continue
            in_quote = not in_quote
        elif char == "/" and not in_quote:
            return value[:index].strip()
        index += 1
    return value.strip()


def _fits_value(card: str) -> str:
    if len(card) < 10 or card[8] != "=":
        return ""
    value = _strip_fits_comment(card[10:])
    if len(value) >= 2 and value.startswith("'") and value.endswith("'"):
        return value[1:-1].replace("''", "'").strip()
    return value.strip()


def _fits_master_hint(path: Path) -> MetadataHint:
    try:
        with path.open("rb") as stream:
            bytes_read = 0
            while bytes_read < MAX_FITS_HEADER_BYTES:
                block = stream.read(2880)
                if not block:
                    break
                bytes_read += len(block)
                for start in range(0, len(block), 80):
                    raw_card = block[start : start + 80]
                    if len(raw_card) < 80:
                        break
                    card = raw_card.decode("ascii", errors="replace")
                    key = card[:8].strip().upper()
                    if key == "END":
                        return MetadataHint(False)
                    if key in IMAGE_TYPE_KEYS:
                        value = _fits_value(card)
                        if _MASTER_RE.search(value):
                            return MetadataHint(True, f"{key}={value}")
            return MetadataHint(False)
    except OSError as exc:
        return MetadataHint(False, warning=f"Could not read FITS header: {exc}")


def _read_xisf_xml_header(path: Path) -> bytes:
    data = bytearray()
    with path.open("rb") as stream:
        while len(data) < MAX_XISF_HEADER_BYTES:
            chunk = stream.read(min(64 * 1024, MAX_XISF_HEADER_BYTES - len(data)))
            if not chunk:
                break
            data.extend(chunk)
            match = _XISF_CLOSE_RE.search(data)
            if match:
                return bytes(data[: match.end()])
    return bytes(data)


def _xisf_master_hint(path: Path) -> MetadataHint:
    try:
        raw_header = _read_xisf_xml_header(path)
    except OSError as exc:
        return MetadataHint(False, warning=f"Could not read XISF header: {exc}")

    if not raw_header:
        return MetadataHint(False, warning="XISF header is empty")
    if not _XISF_CLOSE_RE.search(raw_header):
        return MetadataHint(
            False,
            warning=f"XISF XML header exceeded {MAX_XISF_HEADER_BYTES // (1024 * 1024)} MiB or was incomplete",
        )

    text = raw_header.decode("utf-8", errors="replace")
    for tag_match in _XISF_METADATA_TAG_RE.finditer(text):
        attributes: dict[str, str] = {}
        for match in _XML_ATTRIBUTE_RE.finditer(tag_match.group(0)):
            raw_value = match.group(2) if match.group(2) is not None else match.group(3)
            attributes[match.group(1).lower()] = html.unescape(raw_value or "")

        key = (
            attributes.get("name")
            or attributes.get("keyword")
            or attributes.get("id")
            or ""
        )
        value = attributes.get("value", "").strip("' ")
        normalized_key = key.rsplit(":", 1)[-1].upper()
        if normalized_key in IMAGE_TYPE_KEYS and _MASTER_RE.search(value):
            return MetadataHint(True, f"{key}={value}")
    return MetadataHint(False)


def read_master_metadata_hint(path: Path) -> MetadataHint:
    """Read only enough metadata to determine whether *path* declares a master."""

    suffix = path.suffix.lower()
    if suffix in {".fit", ".fits"}:
        return _fits_master_hint(path)
    if suffix == ".xisf":
        return _xisf_master_hint(path)
    return MetadataHint(False, warning=f"Unsupported extension: {path.suffix}")


def _build_candidate(
    source_root: Path,
    folder: Path,
    image_path: Path,
    *,
    parsed_root: Path | None,
    allow_singleton_review: bool,
    read_metadata: bool,
) -> Candidate | None:
    name_is_master = bool(_MASTER_RE.search(image_path.name))
    metadata = read_master_metadata_hint(image_path) if read_metadata else MetadataHint(False)
    identified_master = name_is_master or metadata.is_master

    # A non-master is useful only as the one image in its directory, where a
    # human may recognize a poorly named/poorly tagged integration. In a
    # multi-image directory it would merely flood the review table with raws.
    if not identified_master and not allow_singleton_review:
        return None

    if name_is_master and metadata.is_master:
        reason = f"Master in filename and metadata ({metadata.detail})"
    elif name_is_master:
        reason = "Master in filename"
    elif metadata.is_master:
        reason = f"Master in metadata ({metadata.detail})"
    else:
        reason = "Only supported image in folder; manual review required"

    try:
        size_bytes = image_path.stat().st_size
    except OSError:
        size_bytes = 0

    relative_folder = folder.relative_to(source_root)
    destination_status = "NOT_CHECKED"
    destination_size: int | None = None
    if parsed_root is not None:
        destination = parsed_root / relative_folder / image_path.name
        try:
            destination_size = destination.stat().st_size
            destination_status = (
                "EXISTS_SAME_SIZE"
                if destination_size == size_bytes
                else "EXISTS_DIFFERENT_SIZE"
            )
        except FileNotFoundError:
            destination_status = "MISSING"
        except OSError as exc:
            destination_status = "DESTINATION_ERROR"
            warning = f"Could not inspect destination {destination}: {exc}"
            metadata = MetadataHint(
                metadata.is_master,
                metadata.detail,
                f"{metadata.warning}; {warning}" if metadata.warning else warning,
            )

    selected = identified_master and destination_status in {"MISSING", "NOT_CHECKED"}

    return Candidate(
        source_root=source_root,
        folder=folder,
        relative_folder=relative_folder,
        image_path=image_path,
        size_bytes=size_bytes,
        selected_by_default=selected,
        confidence="CONFIRMED" if identified_master else "REVIEW",
        reason=reason,
        metadata_warning=metadata.warning,
        destination_status=destination_status,
        destination_size=destination_size,
    )


def scan_source_roots(
    source_roots: Sequence[Path],
    *,
    parsed_root: Path | None = None,
    cancel_event: threading.Event | None = None,
    progress: ProgressCallback | None = None,
    deep_metadata_scan: bool = False,
) -> ScanResult:
    """Find recoverable master images beneath *source_roots*.

    Every file with ``master`` in its filename is a candidate, regardless of how
    many images share its directory. A lone image is opened for a metadata check
    and included for manual review even without a master marker. To avoid making
    antivirus software read terabytes of raw pixels merely because files were
    opened, non-master-named files in multi-image directories are checked only
    when ``deep_metadata_scan`` is explicitly enabled.

    The parsed root may be supplied as a defensive exclusion.  Root overlap is
    rejected because it makes relative-path copying ambiguous and risks a later
    scan rediscovering copied files.
    """

    normalized_roots = [Path(root).resolve(strict=False) for root in source_roots]
    normalized_parsed = parsed_root.resolve(strict=False) if parsed_root else None
    errors = validate_roots(normalized_roots, normalized_parsed)
    if errors:
        raise ValueError("\n".join(errors))

    result = ScanResult()
    for root_index, source_root in enumerate(normalized_roots, start=1):
        walk_errors: list[str] = []

        def on_walk_error(exc: OSError) -> None:
            walk_errors.append(f"{getattr(exc, 'filename', source_root)}: {exc}")

        for directory, subdirectories, filenames in os.walk(
            source_root, topdown=True, followlinks=False, onerror=on_walk_error
        ):
            if cancel_event is not None and cancel_event.is_set():
                raise ScanCancelled()

            folder = Path(directory)
            subdirectories.sort(key=str.casefold)
            filenames.sort(key=str.casefold)

            if normalized_parsed is not None:
                subdirectories[:] = [
                    item
                    for item in subdirectories
                    if not _is_relative_to(folder / item, normalized_parsed)
                ]

            result.directories_scanned += 1
            image_paths = [
                folder / filename
                for filename in filenames
                if (folder / filename).suffix.lower() in SUPPORTED_EXTENSIONS
            ]
            result.image_files_seen += len(image_paths)
            singleton = len(image_paths) == 1
            if singleton:
                result.single_image_directories += 1
            for image_path in image_paths:
                name_is_master = bool(_MASTER_RE.search(image_path.name))
                if not name_is_master and not singleton and not deep_metadata_scan:
                    continue
                candidate = _build_candidate(
                    source_root,
                    folder,
                    image_path,
                    parsed_root=normalized_parsed,
                    allow_singleton_review=singleton,
                    # A master name is already conclusive and does not justify
                    # opening a potentially 700 MiB image. Metadata is needed
                    # for singletons and the explicit deep scan only.
                    read_metadata=not name_is_master,
                )
                if candidate is not None:
                    result.candidates.append(candidate)

            if progress is not None and (
                result.directories_scanned == 1 or result.directories_scanned % 100 == 0
            ):
                progress(
                    root_index,
                    len(normalized_roots),
                    f"Scanning {folder} — {result.directories_scanned:,} folders, "
                    f"{len(result.candidates):,} candidates",
                )

        result.warnings.extend(walk_errors)

    result.candidates.sort(
        key=lambda item: (
            not item.selected_by_default,
            str(item.relative_folder).casefold(),
            item.image_path.name.casefold(),
            str(item.source_root).casefold(),
        )
    )
    if progress is not None:
        progress(
            len(normalized_roots),
            len(normalized_roots),
            f"Scan complete — {result.directories_scanned:,} folders, "
            f"{len(result.candidates):,} candidates",
        )
    return result


def _copy_one_atomic(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.parent / f".{destination.name}.flatparse-{uuid.uuid4().hex}.partial"
    source_before = source.stat()
    try:
        shutil.copy2(source, partial)
        source_after = source.stat()
        partial_size = partial.stat().st_size
        if source_before.st_size != source_after.st_size:
            raise OSError("source size changed during copy")
        if partial_size != source_after.st_size:
            raise OSError(
                f"copied size {partial_size:,} does not match source size {source_after.st_size:,}"
            )
        # Publish without overwriting a file that may have appeared after the
        # preflight check.  Windows rename is no-replace; on other platforms a
        # same-directory hard link gives us the same atomic guarantee.
        if os.name == "nt":
            os.rename(partial, destination)
        else:
            os.link(partial, destination)
            partial.unlink()
    finally:
        if partial.exists():
            partial.unlink()


def copy_candidates(
    candidates: Iterable[Candidate],
    parsed_root: Path,
    *,
    cancel_event: threading.Event | None = None,
    progress: ProgressCallback | None = None,
) -> list[CopyResult]:
    """Copy selected candidates while preserving relative folders.

    Existing files and target collisions are reported and never overwritten.
    """

    selected = list(candidates)
    parsed_root = parsed_root.resolve(strict=False)
    root_errors = validate_roots(
        list(dict.fromkeys(item.source_root.resolve(strict=False) for item in selected)),
        parsed_root,
    )
    if root_errors:
        raise ValueError("\n".join(root_errors))
    parsed_root.mkdir(parents=True, exist_ok=True)

    destinations: dict[str, list[Candidate]] = {}
    for candidate in selected:
        destination = candidate.destination_path(parsed_root)
        destinations.setdefault(_path_key(destination), []).append(candidate)

    colliding_keys = {key for key, items in destinations.items() if len(items) > 1}
    results: list[CopyResult] = []
    for index, candidate in enumerate(selected, start=1):
        if cancel_event is not None and cancel_event.is_set():
            break

        source = candidate.image_path
        destination = candidate.destination_path(parsed_root)
        destination_key = _path_key(destination)
        if destination_key in colliding_keys:
            result = CopyResult(
                source,
                destination,
                "COLLISION",
                candidate.size_bytes,
                "Multiple selected sources map to this destination; nothing was overwritten",
            )
        elif destination.exists():
            try:
                same_size = source.stat().st_size == destination.stat().st_size
            except OSError:
                same_size = False
            result = CopyResult(
                source,
                destination,
                "EXISTS_SAME_SIZE" if same_size else "EXISTS_DIFFERENT_SIZE",
                candidate.size_bytes,
                "Destination already exists; nothing was overwritten",
            )
        elif not source.exists():
            result = CopyResult(
                source,
                destination,
                "ERROR",
                candidate.size_bytes,
                "Source no longer exists",
            )
        else:
            try:
                _copy_one_atomic(source, destination)
                result = CopyResult(
                    source,
                    destination,
                    "COPIED",
                    destination.stat().st_size,
                )
            except OSError as exc:
                result = CopyResult(
                    source,
                    destination,
                    "ERROR",
                    candidate.size_bytes,
                    str(exc),
                )
        results.append(result)
        if progress is not None:
            progress(index, len(selected), f"{result.status}: {source}")
    return results


def write_copy_report(
    results: Sequence[CopyResult],
    parsed_root: Path,
    *,
    candidates: Sequence[Candidate] | None = None,
    selected_keys: set[str] | None = None,
) -> Path:
    """Write an audit CSV beside the copied directory tree.

    When all scan candidates are supplied, unselected and cancelled rows are
    included too.  This makes the report a complete record of the user's review,
    not merely a list of successful copies.
    """

    parsed_root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    path = parsed_root / f"_master_recovery_copy_report_{stamp}.csv"
    with path.open("x", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "selected",
                "rank",
                "detection",
                "status",
                "size_bytes",
                "source_root",
                "relative_folder",
                "source",
                "destination",
                "destination_status_at_scan",
                "destination_size_at_scan",
                "detail",
                "metadata_warning",
            ]
        )
        if candidates is None:
            for result in results:
                writer.writerow(
                    [
                        "yes",
                        "",
                        "",
                        result.status,
                        result.size_bytes,
                        "",
                        "",
                        result.source,
                        result.destination,
                        "",
                        "",
                        result.detail,
                        "",
                    ]
                )
            return path

        result_by_source = {_path_key(result.source): result for result in results}
        effective_selection = selected_keys or set()
        for candidate in candidates:
            selected = candidate.key in effective_selection
            result = result_by_source.get(candidate.key)
            if not selected:
                status = "NOT_SELECTED"
                result = None
            else:
                status = result.status if result is not None else "NOT_ATTEMPTED"
            writer.writerow(
                [
                    "yes" if selected else "no",
                    candidate.confidence,
                    candidate.reason,
                    status,
                    candidate.size_bytes,
                    candidate.source_root,
                    candidate.relative_folder,
                    candidate.image_path,
                    result.destination if result is not None else "",
                    candidate.destination_status,
                    "" if candidate.destination_size is None else candidate.destination_size,
                    result.detail if result is not None else "",
                    candidate.metadata_warning,
                ]
            )
    return path


def human_size(size_bytes: int) -> str:
    value = float(size_bytes)
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if value < 1024 or unit == "TiB":
            return f"{value:.0f} {unit}" if unit == "B" else f"{value:.2f} {unit}"
        value /= 1024
    return f"{value:.2f} TiB"


_TRANSIENT_WINERRORS = frozenset(
    {
        53,    # network path not found
        59,    # unexpected network error
        64,    # network name no longer available
        67,    # network name not found
        121,   # semaphore timeout
        995,   # I/O operation aborted
        1203,  # no network provider accepted the path
        1204,  # incorrect network provider name
        1222,  # network is not present or not started
        1231,  # network location cannot be reached
        1232,  # network location cannot be reached
    }
)
_TRANSIENT_ERRNOS = frozenset(
    value
    for value in (
        errno.EAGAIN,
        errno.EBUSY,
        errno.ECONNABORTED,
        errno.ECONNRESET,
        errno.EHOSTUNREACH,
        errno.EINTR,
        errno.ENETDOWN,
        errno.ENETRESET,
        errno.ENETUNREACH,
        errno.ETIMEDOUT,
        getattr(errno, "ESTALE", None),
    )
    if value is not None
)


def _is_transient_io_error(exc: OSError) -> bool:
    return getattr(exc, "winerror", None) in _TRANSIENT_WINERRORS or exc.errno in _TRANSIENT_ERRNOS


def _wait_for_retry(cancel_event: threading.Event | None, seconds: float) -> None:
    if cancel_event is not None:
        if cancel_event.wait(seconds):
            raise ScanCancelled()
    else:
        time.sleep(seconds)


def _retry_io(
    operation: Callable[[], object],
    *,
    description: str,
    cancel_event: threading.Event | None,
    progress: ProgressCallback | None,
    attempts: int = 4,
) -> object:
    """Retry transient network I/O failures with short exponential backoff."""

    for attempt in range(1, attempts + 1):
        if cancel_event is not None and cancel_event.is_set():
            raise ScanCancelled()
        try:
            return operation()
        except OSError as exc:
            if attempt >= attempts or not _is_transient_io_error(exc):
                raise
            delay = float(2 ** (attempt - 1))
            if progress is not None:
                progress(
                    0,
                    0,
                    f"Network I/O failed while {description}: {exc}. "
                    f"Retry {attempt}/{attempts - 1} in {delay:.0f}s…",
                )
            _wait_for_retry(cancel_event, delay)

    raise AssertionError("unreachable")


def _stat_with_retry(
    path: Path,
    *,
    cancel_event: threading.Event | None,
    progress: ProgressCallback | None,
) -> os.stat_result:
    return _retry_io(
        path.stat,
        description=f"reading file information for {path}",
        cancel_event=cancel_event,
        progress=progress,
    )  # type: ignore[return-value]


def _inventory_comparison_folder(
    root: Path,
    *,
    images_only: bool,
    cancel_event: threading.Event | None,
    progress: ProgressCallback | None,
    label: str,
) -> tuple[dict[str, tuple[Path, Path]], list[str]]:
    files: dict[str, tuple[Path, Path]] = {}
    warnings: list[str] = []
    directory_count = 0
    pending = [root]
    while pending:
        if cancel_event is not None and cancel_event.is_set():
            raise ScanCancelled()
        folder = pending.pop()
        try:
            entries = _retry_io(
                lambda item=folder: list(os.scandir(item)),
                description=f"listing {folder}",
                cancel_event=cancel_event,
                progress=progress,
            )
        except OSError as exc:
            if folder == root:
                raise OSError(f"Could not inventory comparison root {root}: {exc}") from exc
            warnings.append(f"{folder}: {exc}")
            if _is_transient_io_error(exc):
                warnings.append(
                    f"Inventory stopped after the network remained unavailable at {folder}. "
                    "Import/resume this report when the connection returns."
                )
                break
            continue

        directory_count += 1
        directories: list[Path] = []
        file_paths: list[Path] = []
        for entry in entries:
            try:
                if entry.is_dir(follow_symlinks=False):
                    directories.append(Path(entry.path))
                elif entry.is_file(follow_symlinks=False):
                    file_paths.append(Path(entry.path))
            except OSError as exc:
                warnings.append(f"{entry.path}: {exc}")
        # Stack is LIFO, so reverse the sorted directory order for a stable walk.
        pending.extend(sorted(directories, key=lambda item: item.name.casefold(), reverse=True))
        for path in sorted(file_paths, key=lambda item: item.name.casefold()):
            if images_only and path.suffix.lower() not in SUPPORTED_EXTENSIONS:
                continue
            relative = path.relative_to(root)
            key = str(relative).replace("\\", "/").casefold()
            if key in files:
                warnings.append(
                    f"Case-insensitive relative-path collision under {root}: "
                    f"{files[key][0]} and {relative}"
                )
                continue
            files[key] = (relative, path)
        if progress is not None and (directory_count == 1 or directory_count % 250 == 0):
            progress(
                0,
                0,
                f"Inventorying {label}: {directory_count:,} folders, {len(files):,} files",
            )
    return files, warnings


def _hash_file(
    path: Path,
    algorithm: str,
    *,
    cancel_event: threading.Event | None,
    on_bytes: Callable[[int], None] | None,
    progress: ProgressCallback | None,
) -> str:
    def hash_once() -> str:
        before = path.stat()
        digest = hashlib.new(algorithm)
        buffer = bytearray(16 * 1024 * 1024)
        view = memoryview(buffer)
        file_bytes = 0
        with path.open("rb", buffering=0) as stream:
            while True:
                if cancel_event is not None and cancel_event.is_set():
                    raise ScanCancelled()
                count = stream.readinto(buffer)
                if not count:
                    break
                digest.update(view[:count])
                file_bytes += count
                if on_bytes is not None:
                    on_bytes(file_bytes)
        after = path.stat()
        if before.st_size != after.st_size or before.st_mtime_ns != after.st_mtime_ns:
            raise OSError("file changed while it was being hashed")
        return digest.hexdigest()

    return _retry_io(
        hash_once,
        description=f"hashing {path}",
        cancel_event=cancel_event,
        progress=progress,
    )  # type: ignore[return-value]


def compare_folders(
    left_root: Path,
    right_root: Path,
    *,
    algorithm: str = "sha256",
    images_only: bool = False,
    cancel_event: threading.Event | None = None,
    progress: ProgressCallback | None = None,
    previous_result: FolderComparisonResult | None = None,
) -> FolderComparisonResult:
    """Compare two directory trees by relative path and content hash.

    Missing files and size differences do not require hashing.  Every pair with
    equal sizes is fully read and hashed, including zero-byte files.
    """

    left_root = left_root.resolve(strict=False)
    right_root = right_root.resolve(strict=False)
    errors: list[str] = []
    for label, root in (("Left", left_root), ("Right", right_root)):
        try:
            root_stat = _stat_with_retry(
                root, cancel_event=cancel_event, progress=progress
            )
            if not stat.S_ISDIR(root_stat.st_mode):
                errors.append(f"{label} path is not a folder: {root}")
        except FileNotFoundError as exc:
            if _is_transient_io_error(exc):
                errors.append(
                    f"{label} network folder is unavailable after automatic retries: "
                    f"{root}: {exc}"
                )
            else:
                errors.append(f"{label} folder does not exist: {root}")
        except OSError as exc:
            errors.append(f"{label} folder is not accessible after retries: {root}: {exc}")
    if paths_overlap(left_root, right_root):
        errors.append("Comparison folders must be separate and may not overlap.")
    try:
        hashlib.new(algorithm)
    except ValueError:
        errors.append(f"Unsupported hash algorithm: {algorithm}")
    if previous_result is not None:
        if previous_result.left_root.resolve(strict=False) != left_root:
            errors.append("Imported report left root does not match the selected left folder.")
        if previous_result.right_root.resolve(strict=False) != right_root:
            errors.append("Imported report right root does not match the selected right folder.")
        if previous_result.algorithm.lower() != algorithm.lower():
            errors.append("Imported report uses a different hash algorithm.")
        if previous_result.images_only != images_only:
            errors.append("Imported report uses a different file filter.")
    if errors:
        raise ValueError("\n".join(errors))

    left_files, left_warnings = _inventory_comparison_folder(
        left_root,
        images_only=images_only,
        cancel_event=cancel_event,
        progress=progress,
        label="left folder",
    )
    right_files, right_warnings = _inventory_comparison_folder(
        right_root,
        images_only=images_only,
        cancel_event=cancel_event,
        progress=progress,
        label="right folder",
    )

    keys = sorted(set(left_files) | set(right_files))
    file_stats: dict[
        str,
        tuple[int | None, int | None, int | None, int | None],
    ] = {}
    total_hash_bytes = 0
    stat_errors: dict[str, str] = {}
    pending_keys: set[str] = set()
    stat_network_stopped = False
    previous_by_key = (
        {
            str(item.relative_path).replace("\\", "/").casefold(): item
            for item in previous_result.comparisons
        }
        if previous_result is not None
        else {}
    )
    reusable_keys: set[str] = set()
    legacy_cache_reuses = 0
    for key in keys:
        left_entry = left_files.get(key)
        right_entry = right_files.get(key)
        if stat_network_stopped:
            file_stats[key] = (None, None, None, None)
            pending_keys.add(key)
            continue
        try:
            left_stat = (
                _stat_with_retry(
                    left_entry[1], cancel_event=cancel_event, progress=progress
                )
                if left_entry
                else None
            )
            right_stat = (
                _stat_with_retry(
                    right_entry[1], cancel_event=cancel_event, progress=progress
                )
                if right_entry
                else None
            )
            left_size = left_stat.st_size if left_stat else None
            right_size = right_stat.st_size if right_stat else None
            left_mtime = left_stat.st_mtime_ns if left_stat else None
            right_mtime = right_stat.st_mtime_ns if right_stat else None
            file_stats[key] = (left_size, right_size, left_mtime, right_mtime)

            prior = previous_by_key.get(key)
            if (
                prior is not None
                and prior.status in {"MATCH", "CONTENT_DIFFERENT"}
                and prior.left_hash
                and prior.right_hash
                and prior.left_size == left_size
                and prior.right_size == right_size
                and left_entry is not None
                and right_entry is not None
            ):
                timestamps_match = (
                    prior.left_mtime_ns is None
                    or prior.right_mtime_ns is None
                    or (
                        prior.left_mtime_ns == left_mtime
                        and prior.right_mtime_ns == right_mtime
                    )
                )
                if timestamps_match:
                    reusable_keys.add(key)
                    if prior.left_mtime_ns is None or prior.right_mtime_ns is None:
                        legacy_cache_reuses += 1
            if (
                left_size is not None
                and left_size == right_size
                and key not in reusable_keys
            ):
                total_hash_bytes += left_size * 2
        except OSError as exc:
            file_stats[key] = (None, None, None, None)
            stat_errors[key] = str(exc)
            if _is_transient_io_error(exc):
                stat_network_stopped = True
                result_message = (
                    "Network remained unavailable while reading file information; "
                    "remaining files were left pending for resume."
                )
                left_warnings.append(result_message)

    result = FolderComparisonResult(
        left_root=left_root,
        right_root=right_root,
        algorithm=algorithm,
        images_only=images_only,
        left_files=len(left_files),
        right_files=len(right_files),
        inventory_warnings=left_warnings + right_warnings,
    )
    if legacy_cache_reuses:
        result.inventory_warnings.append(
            f"Reused {legacy_cache_reuses:,} completed hash pair(s) from an older CSV "
            "without modification timestamps. Paths and sizes matched, but same-size "
            "changes made after that export cannot be detected without a fresh comparison."
        )
    completed_hash_bytes = 0
    hash_network_stopped = False

    def bytes_hashed(file_bytes: int, base_bytes: int, relative: Path) -> None:
        current_bytes = base_bytes + file_bytes
        result.hashed_bytes = max(result.hashed_bytes, current_bytes)
        if progress is not None:
            progress(
                current_bytes,
                total_hash_bytes,
                f"Hashing {relative} — {human_size(current_bytes)} / "
                f"{human_size(total_hash_bytes)}",
            )

    for index, key in enumerate(keys, start=1):
        if cancel_event is not None and cancel_event.is_set():
            raise ScanCancelled()
        left_entry = left_files.get(key)
        right_entry = right_files.get(key)
        relative = left_entry[0] if left_entry else right_entry[0]
        left_path = left_entry[1] if left_entry else None
        right_path = right_entry[1] if right_entry else None
        left_size, right_size, left_mtime, right_mtime = file_stats[key]

        if hash_network_stopped or key in pending_keys:
            prior = previous_by_key.get(key)
            comparison = FolderComparison(
                relative,
                left_path,
                right_path,
                "PENDING_RETRY",
                left_size if left_size is not None else (prior.left_size if prior else None),
                right_size if right_size is not None else (prior.right_size if prior else None),
                prior.left_hash if prior else "",
                prior.right_hash if prior else "",
                "Not attempted after the network connection was lost",
                left_mtime if left_mtime is not None else (prior.left_mtime_ns if prior else None),
                right_mtime if right_mtime is not None else (prior.right_mtime_ns if prior else None),
            )
        elif key in stat_errors:
            comparison = FolderComparison(
                relative,
                left_path,
                right_path,
                "ERROR",
                detail=f"Could not read file size: {stat_errors[key]}",
                left_mtime_ns=left_mtime,
                right_mtime_ns=right_mtime,
            )
        elif left_entry is None:
            comparison = FolderComparison(
                relative,
                None,
                right_path,
                "RIGHT_ONLY",
                right_size=right_size,
                right_mtime_ns=right_mtime,
            )
        elif right_entry is None:
            comparison = FolderComparison(
                relative,
                left_path,
                None,
                "LEFT_ONLY",
                left_size=left_size,
                left_mtime_ns=left_mtime,
            )
        elif left_size != right_size:
            comparison = FolderComparison(
                relative,
                left_path,
                right_path,
                "SIZE_DIFFERENT",
                left_size=left_size,
                right_size=right_size,
                left_mtime_ns=left_mtime,
                right_mtime_ns=right_mtime,
            )
        elif key in reusable_keys:
            prior = previous_by_key[key]
            comparison = FolderComparison(
                relative,
                left_path,
                right_path,
                prior.status,
                left_size,
                right_size,
                prior.left_hash,
                prior.right_hash,
                "Reused completed hashes from imported report",
                left_mtime,
                right_mtime,
            )
            result.reused_hash_pairs += 1
        else:
            try:
                pair_base = completed_hash_bytes
                left_hash = _hash_file(
                    left_path,
                    algorithm,
                    cancel_event=cancel_event,
                    on_bytes=lambda count, base=pair_base, item=relative: bytes_hashed(
                        count, base, item
                    ),
                    progress=progress,
                )
                completed_hash_bytes += left_size or 0
                right_base = completed_hash_bytes
                right_hash = _hash_file(
                    right_path,
                    algorithm,
                    cancel_event=cancel_event,
                    on_bytes=lambda count, base=right_base, item=relative: bytes_hashed(
                        count, base, item
                    ),
                    progress=progress,
                )
                completed_hash_bytes += right_size or 0
                result.hashed_bytes = completed_hash_bytes
                comparison = FolderComparison(
                    relative,
                    left_path,
                    right_path,
                    "MATCH" if left_hash == right_hash else "CONTENT_DIFFERENT",
                    left_size,
                    right_size,
                    left_hash,
                    right_hash,
                    "",
                    left_mtime,
                    right_mtime,
                )
            except ScanCancelled:
                raise
            except OSError as exc:
                comparison = FolderComparison(
                    relative,
                    left_path,
                    right_path,
                    "ERROR",
                    left_size,
                    right_size,
                    detail=str(exc),
                    left_mtime_ns=left_mtime,
                    right_mtime_ns=right_mtime,
                )
                if _is_transient_io_error(exc):
                    hash_network_stopped = True
                    result.inventory_warnings.append(
                        "Network remained unavailable during hashing; remaining file pairs "
                        "were left as PENDING_RETRY instead of repeatedly timing out."
                    )
        result.comparisons.append(comparison)
        if progress is not None and total_hash_bytes == 0:
            progress(index, len(keys), f"Comparing {relative}")

    result.hashed_bytes = completed_hash_bytes
    if progress is not None:
        progress(
            total_hash_bytes or len(keys),
            total_hash_bytes or len(keys),
            f"Comparison complete — {result.matching_files:,} matches, "
            f"{result.differing_files:,} differences, "
            f"{result.unresolved_files:,} unresolved; "
            f"{result.reused_hash_pairs:,} hash pair(s) reused",
        )
    return result


def write_comparison_report(result: FolderComparisonResult, path: Path) -> Path:
    """Export a complete folder-comparison result as CSV."""

    path = path.resolve(strict=False)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["left_root", result.left_root])
        writer.writerow(["right_root", result.right_root])
        writer.writerow(["algorithm", result.algorithm])
        writer.writerow(["images_only", result.images_only])
        writer.writerow(["inventory_warnings", len(result.inventory_warnings)])
        writer.writerow(["reused_hash_pairs", result.reused_hash_pairs])
        writer.writerow([])
        writer.writerow(
            [
                "status",
                "relative_path",
                "left_size",
                "right_size",
                "left_hash",
                "right_hash",
                "left_path",
                "right_path",
                "detail",
                "left_mtime_ns",
                "right_mtime_ns",
            ]
        )
        for item in result.comparisons:
            writer.writerow(
                [
                    item.status,
                    item.relative_path,
                    "" if item.left_size is None else item.left_size,
                    "" if item.right_size is None else item.right_size,
                    item.left_hash,
                    item.right_hash,
                    item.left_path or "",
                    item.right_path or "",
                    item.detail,
                    "" if item.left_mtime_ns is None else item.left_mtime_ns,
                    "" if item.right_mtime_ns is None else item.right_mtime_ns,
                ]
            )
        if result.inventory_warnings:
            writer.writerow([])
            writer.writerow(["inventory_warning"])
            for warning in result.inventory_warnings:
                writer.writerow([warning])
    return path


def _optional_int(value: str | None) -> int | None:
    if value is None or not value.strip():
        return None
    return int(value)


def read_comparison_report(path: Path) -> FolderComparisonResult:
    """Load a complete CSV export so hashing can resume without starting over."""

    rows: list[list[str]]
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.reader(stream))

    metadata: dict[str, str] = {}
    index = 0
    while index < len(rows) and rows[index]:
        row = rows[index]
        if len(row) >= 2:
            metadata[row[0]] = row[1]
        index += 1
    required = {"left_root", "right_root", "algorithm", "images_only"}
    missing = required - metadata.keys()
    if missing:
        raise ValueError(
            f"Not a supported folder-comparison CSV; missing: {', '.join(sorted(missing))}"
        )

    while index < len(rows) and not rows[index]:
        index += 1
    if index >= len(rows) or "status" not in rows[index]:
        raise ValueError("Not a supported folder-comparison CSV; result header is missing")
    headings = rows[index]
    index += 1
    comparisons: list[FolderComparison] = []
    warnings: list[str] = []
    while index < len(rows):
        row = rows[index]
        index += 1
        if not row:
            break
        values = dict(zip(headings, row))
        relative_text = values.get("relative_path", "")
        if not relative_text:
            continue
        comparisons.append(
            FolderComparison(
                relative_path=Path(relative_text),
                left_path=Path(values["left_path"]) if values.get("left_path") else None,
                right_path=Path(values["right_path"]) if values.get("right_path") else None,
                status=values.get("status", "ERROR"),
                left_size=_optional_int(values.get("left_size")),
                right_size=_optional_int(values.get("right_size")),
                left_hash=values.get("left_hash", ""),
                right_hash=values.get("right_hash", ""),
                detail=values.get("detail", ""),
                left_mtime_ns=_optional_int(values.get("left_mtime_ns")),
                right_mtime_ns=_optional_int(values.get("right_mtime_ns")),
            )
        )

    while index < len(rows):
        row = rows[index]
        index += 1
        if row and row[0] == "inventory_warning":
            continue
        if row:
            warnings.append(row[0])

    images_only = metadata["images_only"].strip().casefold() in {"true", "1", "yes"}
    return FolderComparisonResult(
        left_root=Path(metadata["left_root"]),
        right_root=Path(metadata["right_root"]),
        algorithm=metadata["algorithm"],
        images_only=images_only,
        comparisons=comparisons,
        left_files=sum(item.left_path is not None for item in comparisons),
        right_files=sum(item.right_path is not None for item in comparisons),
        inventory_warnings=warnings,
        reused_hash_pairs=int(metadata.get("reused_hash_pairs", "0") or 0),
    )
