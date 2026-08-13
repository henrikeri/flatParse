"""Tkinter GUI for recovering already-processed masters from source trees."""

from __future__ import annotations

import argparse
import csv
import queue
import threading
import tkinter as tk
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from master_recovery import (
    Candidate,
    CopyResult,
    FolderComparisonResult,
    ScanCancelled,
    ScanResult,
    compare_folders,
    copy_candidates,
    human_size,
    read_comparison_report,
    scan_source_roots,
    validate_roots,
    write_comparison_report,
    write_copy_report,
)


class MasterRecoveryApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("FlatMaster — Recover Existing Masters")
        self.root.geometry("1480x820")
        self.root.minsize(1050, 620)

        self.candidates: list[Candidate] = []
        self.selected_keys: set[str] = set()
        self.item_to_candidate: dict[str, Candidate] = {}
        self.cancel_event = threading.Event()
        self.events: queue.Queue[tuple[str, object]] = queue.Queue()
        self.worker: threading.Thread | None = None
        self.close_when_idle = False

        self.destination_var = tk.StringVar()
        self.deep_metadata_var = tk.BooleanVar(value=False)
        self.status_var = tk.StringVar(value="Add source roots and choose a parsed destination.")
        self.summary_var = tk.StringVar(value="No scan has been run.")
        self.progress_var = tk.DoubleVar(value=0)

        self._build_ui()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.root.after(100, self._poll_events)

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.root, padding=10)
        outer.grid(row=0, column=0, sticky="nsew")
        self.root.rowconfigure(0, weight=1)
        self.root.columnconfigure(0, weight=1)
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(3, weight=1)

        source_frame = ttk.LabelFrame(outer, text="Unprocessed source roots", padding=8)
        source_frame.grid(row=0, column=0, sticky="ew")
        source_frame.columnconfigure(0, weight=1)
        self.source_list = tk.Listbox(source_frame, height=4, selectmode=tk.EXTENDED)
        self.source_list.grid(row=0, column=0, rowspan=3, sticky="nsew", padx=(0, 8))
        ttk.Button(source_frame, text="Add folder…", command=self._add_source).grid(
            row=0, column=1, sticky="ew"
        )
        ttk.Button(source_frame, text="Remove selected", command=self._remove_source).grid(
            row=1, column=1, sticky="ew", pady=4
        )
        ttk.Button(source_frame, text="Clear", command=lambda: self.source_list.delete(0, tk.END)).grid(
            row=2, column=1, sticky="ew"
        )

        destination_frame = ttk.Frame(outer, padding=(0, 10, 0, 8))
        destination_frame.grid(row=1, column=0, sticky="ew")
        destination_frame.columnconfigure(1, weight=1)
        ttk.Label(destination_frame, text="Parsed destination:").grid(row=0, column=0, padx=(0, 8))
        ttk.Entry(destination_frame, textvariable=self.destination_var).grid(
            row=0, column=1, sticky="ew"
        )
        ttk.Button(destination_frame, text="Browse…", command=self._choose_destination).grid(
            row=0, column=2, padx=(8, 0)
        )

        action_frame = ttk.Frame(outer)
        action_frame.grid(row=2, column=0, sticky="ew", pady=(0, 8))
        self.scan_button = ttk.Button(action_frame, text="Scan", command=self._start_scan)
        self.scan_button.pack(side=tk.LEFT)
        self.cancel_button = ttk.Button(
            action_frame, text="Cancel", command=self._cancel, state=tk.DISABLED
        )
        self.cancel_button.pack(side=tk.LEFT, padx=(6, 16))
        ttk.Button(
            action_frame,
            text="Compare two folders…",
            command=lambda: FolderComparisonWindow(self.root),
        ).pack(side=tk.LEFT, padx=(0, 16))
        ttk.Checkbutton(
            action_frame,
            text="Deep metadata scan (slow)",
            variable=self.deep_metadata_var,
        ).pack(side=tk.LEFT, padx=(0, 16))
        ttk.Button(action_frame, text="Select confirmed", command=self._select_confirmed).pack(
            side=tk.LEFT
        )
        ttk.Button(action_frame, text="Select all", command=self._select_all).pack(
            side=tk.LEFT, padx=6
        )
        ttk.Button(action_frame, text="Clear selection", command=self._clear_selection).pack(
            side=tk.LEFT
        )
        self.copy_button = ttk.Button(
            action_frame, text="Copy selected", command=self._start_copy, state=tk.DISABLED
        )
        self.copy_button.pack(side=tk.RIGHT)

        table_frame = ttk.Frame(outer)
        table_frame.grid(row=3, column=0, sticky="nsew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        columns = (
            "selected",
            "confidence",
            "reason",
            "file",
            "folder",
            "source",
            "size",
            "destination",
            "result",
        )
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", selectmode="extended")
        headings = {
            "selected": "Use",
            "confidence": "Rank",
            "reason": "Detection",
            "file": "File",
            "folder": "Relative folder",
            "source": "Source root",
            "size": "Size",
            "destination": "Parsed status",
            "result": "Copy result",
        }
        widths = {
            "selected": 48,
            "confidence": 90,
            "reason": 270,
            "file": 280,
            "folder": 260,
            "source": 220,
            "size": 85,
            "destination": 150,
            "result": 135,
        }
        for column in columns:
            self.tree.heading(column, text=headings[column], command=lambda c=column: self._sort(c))
            self.tree.column(column, width=widths[column], minwidth=45, stretch=column not in {"selected", "confidence", "size"})
        self.tree.grid(row=0, column=0, sticky="nsew")
        vertical = ttk.Scrollbar(table_frame, orient=tk.VERTICAL, command=self.tree.yview)
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal = ttk.Scrollbar(table_frame, orient=tk.HORIZONTAL, command=self.tree.xview)
        horizontal.grid(row=1, column=0, sticky="ew")
        self.tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.tree.bind("<Double-1>", self._toggle_event)
        self.tree.bind("<space>", self._toggle_event)
        self.tree.tag_configure("confirmed", background="#e9f7e9")
        self.tree.tag_configure("review", background="#fff7df")
        self.tree.tag_configure("existing", background="#eef3f7", foreground="#58636d")
        self.tree.tag_configure("conflict", background="#ffe5e5", foreground="#8a0000")
        self.tree.tag_configure("warning", foreground="#9a5a00")

        footer = ttk.Frame(outer, padding=(0, 8, 0, 0))
        footer.grid(row=4, column=0, sticky="ew")
        footer.columnconfigure(0, weight=1)
        ttk.Label(footer, textvariable=self.summary_var).grid(row=0, column=0, sticky="w")
        self.progress = ttk.Progressbar(footer, variable=self.progress_var, maximum=100, length=260)
        self.progress.grid(row=0, column=1, padx=(8, 0))
        ttk.Label(footer, textvariable=self.status_var).grid(
            row=1, column=0, columnspan=2, sticky="ew", pady=(5, 0)
        )

    def _source_roots(self) -> list[Path]:
        return [Path(value) for value in self.source_list.get(0, tk.END)]

    def _add_source(self) -> None:
        chosen = filedialog.askdirectory(title="Choose an unprocessed source root", mustexist=True)
        if chosen and chosen.casefold() not in {item.casefold() for item in self.source_list.get(0, tk.END)}:
            self.source_list.insert(tk.END, chosen)

    def _remove_source(self) -> None:
        for index in reversed(self.source_list.curselection()):
            self.source_list.delete(index)

    def _choose_destination(self) -> None:
        chosen = filedialog.askdirectory(title="Choose the parsed destination", mustexist=False)
        if chosen:
            self.destination_var.set(chosen)

    def _configuration(self) -> tuple[list[Path], Path] | None:
        roots = self._source_roots()
        raw_destination = self.destination_var.get().strip()
        if not raw_destination:
            messagebox.showerror("Missing destination", "Choose the parsed destination folder.")
            return None
        destination = Path(raw_destination)
        errors = validate_roots(roots, destination)
        if errors:
            messagebox.showerror("Invalid folders", "\n\n".join(errors))
            return None
        return roots, destination

    def _set_busy(self, busy: bool) -> None:
        self.scan_button.configure(state=tk.DISABLED if busy else tk.NORMAL)
        self.cancel_button.configure(state=tk.NORMAL if busy else tk.DISABLED)
        self.copy_button.configure(
            state=tk.DISABLED if busy or not self.selected_keys else tk.NORMAL
        )
        if busy:
            self.progress.configure(mode="indeterminate")
            self.progress.start(12)
        else:
            self.progress.stop()
            self.progress.configure(mode="determinate")
            self.progress_var.set(0)

    def _start_scan(self) -> None:
        configuration = self._configuration()
        if configuration is None:
            return
        roots, destination = configuration
        self.cancel_event = threading.Event()
        self._set_busy(True)
        self.status_var.set("Scanning headers…")
        self.worker = threading.Thread(
            target=self._scan_worker,
            args=(roots, destination, self.deep_metadata_var.get()),
            name="master-recovery-scan",
            daemon=True,
        )
        self.worker.start()

    def _scan_worker(
        self, roots: list[Path], destination: Path, deep_metadata_scan: bool
    ) -> None:
        try:
            result = scan_source_roots(
                roots,
                parsed_root=destination,
                cancel_event=self.cancel_event,
                deep_metadata_scan=deep_metadata_scan,
                progress=lambda _current, _total, message: self.events.put(
                    ("progress", (0, 0, message))
                ),
            )
            self.events.put(("scan_complete", result))
        except ScanCancelled:
            self.events.put(("cancelled", "Scan cancelled."))
        except Exception as exc:  # keep worker failures visible in the GUI
            self.events.put(("error", f"Scan failed:\n\n{exc}"))

    def _populate(self, result: ScanResult) -> None:
        self.tree.delete(*self.tree.get_children())
        self.candidates = result.candidates
        self.selected_keys = {
            candidate.key for candidate in self.candidates if candidate.selected_by_default
        }
        self.item_to_candidate.clear()
        for index, candidate in enumerate(self.candidates):
            item_id = f"candidate-{index}"
            if candidate.destination_status == "EXISTS_SAME_SIZE":
                tags = ["existing"]
            elif candidate.destination_status in {
                "EXISTS_DIFFERENT_SIZE",
                "DESTINATION_ERROR",
            }:
                tags = ["conflict"]
            else:
                tags = ["confirmed" if candidate.confidence == "CONFIRMED" else "review"]
            if candidate.metadata_warning:
                tags.append("warning")
            self.tree.insert(
                "",
                tk.END,
                iid=item_id,
                values=(
                    "☑" if candidate.key in self.selected_keys else "☐",
                    candidate.confidence,
                    candidate.reason,
                    candidate.image_path.name,
                    str(candidate.relative_folder),
                    str(candidate.source_root),
                    human_size(candidate.size_bytes),
                    candidate.destination_status,
                    "",
                ),
                tags=tuple(tags),
            )
            self.item_to_candidate[item_id] = candidate
        selected = sum(item.selected_by_default for item in self.candidates)
        confirmed = sum(item.confidence == "CONFIRMED" for item in self.candidates)
        review = sum(item.confidence == "REVIEW" for item in self.candidates)
        missing = sum(item.destination_status == "MISSING" for item in self.candidates)
        existing = sum(item.destination_status == "EXISTS_SAME_SIZE" for item in self.candidates)
        conflicts = sum(
            item.destination_status in {"EXISTS_DIFFERENT_SIZE", "DESTINATION_ERROR"}
            for item in self.candidates
        )
        self.summary_var.set(
            f"{len(self.candidates):,} candidates: {confirmed:,} confirmed, {review:,} review; "
            f"{missing:,} missing, {existing:,} already present, {conflicts:,} conflicts; "
            f"{selected:,} selected; {result.image_files_seen:,} image files seen"
        )
        warning_suffix = f"; {len(result.warnings):,} inaccessible paths" if result.warnings else ""
        self.status_var.set(
            f"Scanned {result.directories_scanned:,} folders{warning_suffix}. "
            "Double-click a row or press Space to toggle it."
        )
        self._update_copy_button()

    def _toggle_event(self, event: tk.Event) -> str:
        if event.type == tk.EventType.ButtonPress:
            row = self.tree.identify_row(event.y)
            if row:
                self.tree.selection_set(row)
        for item_id in self.tree.selection():
            candidate = self.item_to_candidate.get(item_id)
            if candidate is None:
                continue
            if candidate.key in self.selected_keys:
                self.selected_keys.remove(candidate.key)
            else:
                self.selected_keys.add(candidate.key)
            self.tree.set(item_id, "selected", "☑" if candidate.key in self.selected_keys else "☐")
        self._update_copy_button()
        return "break"

    def _select_confirmed(self) -> None:
        self.selected_keys = {item.key for item in self.candidates if item.selected_by_default}
        self._refresh_checks()

    def _select_all(self) -> None:
        self.selected_keys = {item.key for item in self.candidates}
        self._refresh_checks()

    def _clear_selection(self) -> None:
        self.selected_keys.clear()
        self._refresh_checks()

    def _refresh_checks(self) -> None:
        for item_id, candidate in self.item_to_candidate.items():
            self.tree.set(item_id, "selected", "☑" if candidate.key in self.selected_keys else "☐")
        self._update_copy_button()

    def _update_copy_button(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            state = tk.DISABLED
        else:
            state = tk.NORMAL if self.selected_keys else tk.DISABLED
        self.copy_button.configure(state=state)

    def _sort(self, column: str) -> None:
        items = list(self.tree.get_children())
        reverse = self.tree.heading(column, "text").endswith(" ▼")
        for name, heading in {
            "selected": "Use",
            "confidence": "Rank",
            "reason": "Detection",
            "file": "File",
            "folder": "Relative folder",
            "source": "Source root",
            "size": "Size",
            "destination": "Parsed status",
            "result": "Copy result",
        }.items():
            self.tree.heading(name, text=heading)
        base = self.tree.heading(column, "text")
        self.tree.heading(column, text=f"{base} {'▲' if reverse else '▼'}")
        items.sort(key=lambda item: self.tree.set(item, column).casefold(), reverse=reverse)
        for index, item in enumerate(items):
            self.tree.move(item, "", index)

    def _start_copy(self) -> None:
        configuration = self._configuration()
        if configuration is None:
            return
        _, destination = configuration
        selected = [item for item in self.candidates if item.key in self.selected_keys]
        if not selected:
            return
        total_bytes = sum(item.size_bytes for item in selected)
        if not messagebox.askyesno(
            "Confirm copy",
            f"Copy {len(selected):,} selected master candidate(s) "
            f"({human_size(total_bytes)}) into:\n\n{destination}\n\n"
            "Relative folder paths will be preserved. Existing files will not be overwritten.",
        ):
            return
        self.cancel_event = threading.Event()
        self._set_busy(True)
        self.status_var.set("Copying selected files…")
        self.worker = threading.Thread(
            target=self._copy_worker,
            args=(selected, list(self.candidates), set(self.selected_keys), destination),
            name="master-recovery-copy",
            daemon=True,
        )
        self.worker.start()

    def _copy_worker(
        self,
        selected: list[Candidate],
        all_candidates: list[Candidate],
        selected_keys: set[str],
        destination: Path,
    ) -> None:
        try:
            results = copy_candidates(
                selected,
                destination,
                cancel_event=self.cancel_event,
                progress=lambda current, total, message: self.events.put(
                    ("progress", (current, total, message))
                ),
            )
            report_path = write_copy_report(
                results,
                destination,
                candidates=all_candidates,
                selected_keys=selected_keys,
            )
            self.events.put(("copy_complete", (results, report_path, len(selected))))
        except Exception as exc:
            self.events.put(("error", f"Copy failed:\n\n{exc}"))

    def _show_copy_results(
        self, results: list[CopyResult], report_path: Path, requested_count: int
    ) -> None:
        by_source = {str(result.source).casefold(): result for result in results}
        for item_id, candidate in self.item_to_candidate.items():
            result = by_source.get(str(candidate.image_path).casefold())
            if result is not None:
                self.tree.set(item_id, "result", result.status)
        copied = sum(result.status == "COPIED" for result in results)
        existing = sum(result.status.startswith("EXISTS_") for result in results)
        collisions = sum(result.status == "COLLISION" for result in results)
        errors = sum(result.status == "ERROR" for result in results)
        cancelled = len(results) < requested_count
        self.summary_var.set(
            f"Copy result: {copied:,} copied, {existing:,} already existed, "
            f"{collisions:,} collisions, {errors:,} errors"
        )
        self.status_var.set(f"Audit report: {report_path}")
        message = (
            f"Copied: {copied:,}\nAlready present: {existing:,}\n"
            f"Destination collisions: {collisions:,}\nErrors: {errors:,}\n"
        )
        if cancelled:
            message += f"Not attempted after cancellation: {requested_count - len(results):,}\n"
        message += f"\nAudit report:\n{report_path}"
        if errors or collisions:
            messagebox.showwarning("Copy completed with issues", message)
        else:
            messagebox.showinfo("Copy completed", message)

    def _cancel(self) -> None:
        self.cancel_event.set()
        self.status_var.set("Cancellation requested; finishing the current file operation…")

    def _on_close(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            if messagebox.askyesno(
                "Operation in progress",
                "Request cancellation and close after the current file operation finishes?",
            ):
                self.close_when_idle = True
                self._cancel()
            return
        self.root.destroy()

    def _poll_events(self) -> None:
        try:
            while True:
                event_name, payload = self.events.get_nowait()
                if event_name == "progress":
                    current, total, message = payload  # type: ignore[misc]
                    self.status_var.set(str(message))
                    if total:
                        self.progress.stop()
                        self.progress.configure(mode="determinate")
                        self.progress_var.set(100 * int(current) / int(total))
                elif event_name == "scan_complete":
                    self.worker = None
                    self._set_busy(False)
                    if not self.close_when_idle:
                        self._populate(payload)  # type: ignore[arg-type]
                elif event_name == "copy_complete":
                    self.worker = None
                    self._set_busy(False)
                    if not self.close_when_idle:
                        results, report_path, requested_count = payload  # type: ignore[misc]
                        self._show_copy_results(results, report_path, requested_count)
                elif event_name == "cancelled":
                    self.worker = None
                    self._set_busy(False)
                    self.status_var.set(str(payload))
                elif event_name == "error":
                    self.worker = None
                    self._set_busy(False)
                    self.status_var.set("Operation failed.")
                    if not self.close_when_idle:
                        messagebox.showerror("FlatMaster recovery", str(payload))
        except queue.Empty:
            pass
        if self.close_when_idle and self.worker is None:
            self.root.destroy()
            return
        self.root.after(100, self._poll_events)


class FolderComparisonWindow:
    """Independent, read-only folder hash-comparison window."""

    ALGORITHMS = {"SHA-256": "sha256", "BLAKE2b": "blake2b"}

    def __init__(self, parent: tk.Misc) -> None:
        self.window = tk.Toplevel(parent)
        self.window.title("FlatMaster — Compare Two Folders by Hash")
        self.window.geometry("1420x760")
        self.window.minsize(950, 560)

        self.left_var = tk.StringVar()
        self.right_var = tk.StringVar()
        self.algorithm_var = tk.StringVar(value="SHA-256")
        self.images_only_var = tk.BooleanVar(value=False)
        self.differences_only_var = tk.BooleanVar(value=True)
        self.status_var = tk.StringVar(
            value="Choose two separate folders. Equal-sized file pairs will be read in full."
        )
        self.summary_var = tk.StringVar(value="No comparison has been run.")
        self.progress_var = tk.DoubleVar(value=0)
        self.events: queue.Queue[tuple[str, object]] = queue.Queue()
        self.cancel_event = threading.Event()
        self.worker: threading.Thread | None = None
        self.result: FolderComparisonResult | None = None
        self.close_when_idle = False

        self._build_ui()
        self.window.protocol("WM_DELETE_WINDOW", self._on_close)
        self.window.after(100, self._poll_events)

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.window, padding=10)
        outer.grid(row=0, column=0, sticky="nsew")
        self.window.rowconfigure(0, weight=1)
        self.window.columnconfigure(0, weight=1)
        outer.columnconfigure(1, weight=1)
        outer.rowconfigure(4, weight=1)

        ttk.Label(outer, text="Left folder:").grid(row=0, column=0, sticky="w", padx=(0, 8))
        ttk.Entry(outer, textvariable=self.left_var).grid(row=0, column=1, sticky="ew")
        ttk.Button(outer, text="Browse…", command=lambda: self._browse(self.left_var, "left")).grid(
            row=0, column=2, padx=(8, 0)
        )

        ttk.Label(outer, text="Right folder:").grid(
            row=1, column=0, sticky="w", padx=(0, 8), pady=(6, 0)
        )
        ttk.Entry(outer, textvariable=self.right_var).grid(
            row=1, column=1, sticky="ew", pady=(6, 0)
        )
        ttk.Button(
            outer,
            text="Browse…",
            command=lambda: self._browse(self.right_var, "right"),
        ).grid(row=1, column=2, padx=(8, 0), pady=(6, 0))

        settings = ttk.Frame(outer, padding=(0, 10, 0, 8))
        settings.grid(row=2, column=0, columnspan=3, sticky="ew")
        ttk.Label(settings, text="Hash:").pack(side=tk.LEFT)
        ttk.Combobox(
            settings,
            textvariable=self.algorithm_var,
            values=tuple(self.ALGORITHMS),
            state="readonly",
            width=12,
        ).pack(side=tk.LEFT, padx=(6, 18))
        ttk.Checkbutton(
            settings,
            text="Only FIT/FITS/XISF images",
            variable=self.images_only_var,
        ).pack(side=tk.LEFT)
        ttk.Checkbutton(
            settings,
            text="Show differences only",
            variable=self.differences_only_var,
            command=self._render_result,
        ).pack(side=tk.LEFT, padx=(18, 0))

        actions = ttk.Frame(outer)
        actions.grid(row=3, column=0, columnspan=3, sticky="ew", pady=(0, 8))
        self.compare_button = ttk.Button(actions, text="Compare folders", command=self._start)
        self.compare_button.pack(side=tk.LEFT)
        self.retry_button = ttk.Button(
            actions, text="Retry / resume", command=self._retry, state=tk.DISABLED
        )
        self.retry_button.pack(side=tk.LEFT, padx=(6, 0))
        self.cancel_button = ttk.Button(
            actions, text="Cancel", command=self._cancel, state=tk.DISABLED
        )
        self.cancel_button.pack(side=tk.LEFT, padx=6)
        ttk.Button(actions, text="Import complete CSV…", command=self._import).pack(
            side=tk.LEFT, padx=(10, 0)
        )
        self.export_button = ttk.Button(
            actions, text="Export complete CSV…", command=self._export, state=tk.DISABLED
        )
        self.export_button.pack(side=tk.RIGHT)

        table_frame = ttk.Frame(outer)
        table_frame.grid(row=4, column=0, columnspan=3, sticky="nsew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        columns = (
            "status",
            "relative",
            "left_size",
            "right_size",
            "left_hash",
            "right_hash",
            "detail",
        )
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings")
        headings = {
            "status": "Status",
            "relative": "Relative path",
            "left_size": "Left size",
            "right_size": "Right size",
            "left_hash": "Left hash",
            "right_hash": "Right hash",
            "detail": "Details",
        }
        widths = {
            "status": 145,
            "relative": 330,
            "left_size": 100,
            "right_size": 100,
            "left_hash": 270,
            "right_hash": 270,
            "detail": 260,
        }
        for column in columns:
            self.tree.heading(column, text=headings[column])
            self.tree.column(column, width=widths[column], minwidth=70)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vertical = ttk.Scrollbar(table_frame, orient=tk.VERTICAL, command=self.tree.yview)
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal = ttk.Scrollbar(table_frame, orient=tk.HORIZONTAL, command=self.tree.xview)
        horizontal.grid(row=1, column=0, sticky="ew")
        self.tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.tree.tag_configure("match", background="#e9f7e9")
        self.tree.tag_configure("difference", background="#fff1dc")
        self.tree.tag_configure("missing", background="#fff7df")
        self.tree.tag_configure("error", background="#ffe5e5", foreground="#8a0000")

        footer = ttk.Frame(outer, padding=(0, 8, 0, 0))
        footer.grid(row=5, column=0, columnspan=3, sticky="ew")
        footer.columnconfigure(0, weight=1)
        ttk.Label(footer, textvariable=self.summary_var).grid(row=0, column=0, sticky="w")
        self.progress = ttk.Progressbar(
            footer, variable=self.progress_var, maximum=100, length=320
        )
        self.progress.grid(row=0, column=1, padx=(8, 0))
        ttk.Label(footer, textvariable=self.status_var).grid(
            row=1, column=0, columnspan=2, sticky="ew", pady=(5, 0)
        )

    def _browse(self, variable: tk.StringVar, side: str) -> None:
        chosen = filedialog.askdirectory(
            parent=self.window,
            title=f"Choose the {side} comparison folder",
            mustexist=True,
        )
        if chosen:
            variable.set(chosen)

    def _set_busy(self, busy: bool) -> None:
        self.compare_button.configure(state=tk.DISABLED if busy else tk.NORMAL)
        self.cancel_button.configure(state=tk.NORMAL if busy else tk.DISABLED)
        self.retry_button.configure(
            state=tk.DISABLED if busy or self.result is None else tk.NORMAL
        )
        self.export_button.configure(
            state=tk.DISABLED if busy or self.result is None else tk.NORMAL
        )
        if busy:
            self.progress.configure(mode="indeterminate")
            self.progress.start(12)
        else:
            self.progress.stop()
            self.progress.configure(mode="determinate")
            self.progress_var.set(0)

    def _start(self) -> None:
        self._begin(previous_result=None)

    def _retry(self) -> None:
        if self.result is None:
            return
        self._begin(previous_result=self.result)

    def _begin(self, previous_result: FolderComparisonResult | None) -> None:
        left_text = self.left_var.get().strip()
        right_text = self.right_var.get().strip()
        if not left_text or not right_text:
            messagebox.showerror(
                "Missing folders", "Choose both comparison folders.", parent=self.window
            )
            return
        left = Path(left_text)
        right = Path(right_text)
        if previous_result is None:
            self.result = None
            self.tree.delete(*self.tree.get_children())
            self.summary_var.set("Comparison running…")
        else:
            self.summary_var.set(
                "Resuming comparison; completed hashes will be reused when files are unchanged…"
            )
        self.cancel_event = threading.Event()
        self._set_busy(True)
        self.worker = threading.Thread(
            target=self._worker,
            args=(
                left,
                right,
                self.ALGORITHMS[self.algorithm_var.get()],
                self.images_only_var.get(),
                previous_result,
            ),
            name="folder-hash-comparison",
            daemon=True,
        )
        self.worker.start()

    def _worker(
        self,
        left: Path,
        right: Path,
        algorithm: str,
        images_only: bool,
        previous_result: FolderComparisonResult | None,
    ) -> None:
        try:
            result = compare_folders(
                left,
                right,
                algorithm=algorithm,
                images_only=images_only,
                cancel_event=self.cancel_event,
                previous_result=previous_result,
                progress=lambda current, total, message: self.events.put(
                    ("progress", (current, total, message))
                ),
            )
            self.events.put(("complete", result))
        except ScanCancelled:
            self.events.put(("cancelled", "Comparison cancelled."))
        except Exception as exc:
            self.events.put(("error", f"Comparison failed:\n\n{exc}"))

    def _render_result(self) -> None:
        self.tree.delete(*self.tree.get_children())
        if self.result is None:
            return
        differences_only = self.differences_only_var.get()
        displayed = 0
        for item in self.result.comparisons:
            if differences_only and item.status == "MATCH":
                continue
            if item.status == "MATCH":
                tag = "match"
            elif item.status in {"LEFT_ONLY", "RIGHT_ONLY"}:
                tag = "missing"
            elif item.status in {"ERROR", "PENDING_RETRY"}:
                tag = "error"
            else:
                tag = "difference"
            self.tree.insert(
                "",
                tk.END,
                values=(
                    item.status,
                    str(item.relative_path),
                    "—" if item.left_size is None else human_size(item.left_size),
                    "—" if item.right_size is None else human_size(item.right_size),
                    item.left_hash,
                    item.right_hash,
                    item.detail,
                ),
                tags=(tag,),
            )
            displayed += 1
        filter_text = "differences" if differences_only else "rows"
        self.status_var.set(f"Showing {displayed:,} {filter_text}.")

    def _finish(self, result: FolderComparisonResult) -> None:
        self.result = result
        self._set_busy(False)
        statuses: dict[str, int] = {}
        for item in result.comparisons:
            statuses[item.status] = statuses.get(item.status, 0) + 1
        self.summary_var.set(
            f"{result.matching_files:,} exact matches; {result.differing_files:,} differences "
            f"({statuses.get('CONTENT_DIFFERENT', 0):,} content, "
            f"{statuses.get('SIZE_DIFFERENT', 0):,} size, "
            f"{statuses.get('LEFT_ONLY', 0):,} left-only, "
            f"{statuses.get('RIGHT_ONLY', 0):,} right-only, "
            f"{statuses.get('ERROR', 0):,} errors, "
            f"{statuses.get('PENDING_RETRY', 0):,} pending); "
            f"{result.reused_hash_pairs:,} completed hash pair(s) reused"
        )
        self._render_result()
        if result.inventory_warnings:
            messagebox.showwarning(
                "Comparison completed with inventory warnings",
                f"The comparison encountered {len(result.inventory_warnings):,} inaccessible or "
                "ambiguous path(s). Export the CSV to see them. The result should not be treated "
                "as proof that the trees are complete.",
                parent=self.window,
            )

    def _import(self) -> None:
        chosen = filedialog.askopenfilename(
            parent=self.window,
            title="Import a complete folder-comparison CSV",
            filetypes=(("CSV files", "*.csv"), ("All files", "*.*")),
        )
        if not chosen:
            return
        try:
            result = read_comparison_report(Path(chosen))
        except (OSError, ValueError, csv.Error) as exc:
            messagebox.showerror("Import failed", str(exc), parent=self.window)
            return
        self.result = result
        self.left_var.set(str(result.left_root))
        self.right_var.set(str(result.right_root))
        algorithm_label = next(
            (
                label
                for label, value in self.ALGORITHMS.items()
                if value.casefold() == result.algorithm.casefold()
            ),
            None,
        )
        if algorithm_label is None:
            messagebox.showerror(
                "Unsupported report",
                f"This GUI does not offer the report's hash algorithm: {result.algorithm}",
                parent=self.window,
            )
            self.result = None
            return
        self.algorithm_var.set(algorithm_label)
        self.images_only_var.set(result.images_only)
        self._set_busy(False)
        self._render_result()
        self.summary_var.set(
            f"Imported {len(result.comparisons):,} completed/result rows from {chosen}"
        )
        self.status_var.set(
            "Press Retry / resume. Folder names and sizes will be inventoried again, but "
            "completed hashes will be reused when the recorded files still match."
        )

    def _export(self) -> None:
        if self.result is None:
            return
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        chosen = filedialog.asksaveasfilename(
            parent=self.window,
            title="Export complete folder comparison",
            defaultextension=".csv",
            filetypes=(("CSV files", "*.csv"), ("All files", "*.*")),
            initialfile=f"folder_comparison_{stamp}.csv",
        )
        if not chosen:
            return
        try:
            report = write_comparison_report(self.result, Path(chosen))
            self.status_var.set(f"Complete CSV exported to {report}")
        except OSError as exc:
            messagebox.showerror("Export failed", str(exc), parent=self.window)

    def _cancel(self) -> None:
        self.cancel_event.set()
        self.status_var.set("Cancellation requested; stopping after the current read block…")

    def _on_close(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            if messagebox.askyesno(
                "Comparison in progress",
                "Cancel the comparison and close?",
                parent=self.window,
            ):
                self.close_when_idle = True
                self._cancel()
            return
        self.window.destroy()

    def _poll_events(self) -> None:
        if not self.window.winfo_exists():
            return
        try:
            while True:
                event_name, payload = self.events.get_nowait()
                if event_name == "progress":
                    current, total, message = payload  # type: ignore[misc]
                    self.status_var.set(str(message))
                    if total:
                        self.progress.stop()
                        self.progress.configure(mode="determinate")
                        self.progress_var.set(min(100, 100 * int(current) / int(total)))
                elif event_name == "complete":
                    self.worker = None
                    if not self.close_when_idle:
                        self._finish(payload)  # type: ignore[arg-type]
                elif event_name == "cancelled":
                    self.worker = None
                    self._set_busy(False)
                    self.status_var.set(str(payload))
                elif event_name == "error":
                    self.worker = None
                    self._set_busy(False)
                    self.status_var.set("Comparison failed.")
                    if not self.close_when_idle:
                        messagebox.showerror(
                            "Folder comparison", str(payload), parent=self.window
                        )
        except queue.Empty:
            pass
        if self.close_when_idle and self.worker is None:
            self.window.destroy()
            return
        self.window.after(100, self._poll_events)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Open the FlatMaster existing-master recovery GUI."
    )
    parser.add_argument(
        "source",
        nargs="*",
        type=Path,
        help="optional source roots to pre-populate",
    )
    parser.add_argument(
        "--destination",
        type=Path,
        help="optional parsed destination to pre-populate",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    root = tk.Tk()
    app = MasterRecoveryApp(root)
    for source in args.source:
        app.source_list.insert(tk.END, str(source))
    if args.destination is not None:
        app.destination_var.set(str(args.destination))
    root.mainloop()


if __name__ == "__main__":
    main()
