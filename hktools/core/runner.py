"""Пакетный запуск инструмента: последовательно или параллельно, с отменой."""

from __future__ import annotations

import concurrent.futures as cf
import threading
import time
import traceback
from dataclasses import dataclass, field
from typing import Any, Callable

from ..config import settings as app_settings
from ..log import log
from ..media.process import CancelToken, Cancelled
from ..tools import ToolMissing
from . import desktop
from .script import Batch, Context, QueueItem, Result, Script


@dataclass
class BatchSummary:
    total: int = 0
    done: int = 0
    partial: int = 0
    failed: int = 0
    skipped: int = 0
    cancelled: bool = False
    elapsed: float = 0.0
    results: dict[int, Result] = field(default_factory=dict)

    @property
    def ok(self) -> int:
        return self.done + self.partial


class BatchRunner:
    """Выполняет script.process для каждого элемента в рабочих потоках.

    Колбэки вызываются из рабочих потоков — UI должен перекидывать их в главный цикл.
    """

    def __init__(
        self,
        script: Script,
        items: list[QueueItem],
        values: dict[str, Any],
        out_dir: str | None = None,
        *,
        on_item: Callable[[QueueItem], None] | None = None,
        on_overall: Callable[[float, str], None] | None = None,
        on_result: Callable[[QueueItem, Result], None] | None = None,
        on_finish: Callable[[BatchSummary], None] | None = None,
        ask: Callable[..., Any] | None = None,
        parallel: int | None = None,
    ) -> None:
        self.script = script
        self.all_items = items
        self.values = values
        self.out_dir = out_dir
        self.on_item = on_item
        self.on_overall = on_overall
        self.on_result = on_result
        self.on_finish = on_finish
        self.ask = ask
        self.token = CancelToken()
        self.summary = BatchSummary()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()
        self._progress: dict[int, float] = {}
        if parallel is None:
            parallel = int(app_settings.get("max_parallel") or 1) if app_settings.get("enable_parallel") else 1
        self.parallel = max(1, parallel)

    # --- управление ---
    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name=f"batch-{self.script.id}", daemon=True)
        self._thread.start()

    def run_blocking(self) -> BatchSummary:
        self._run()
        return self.summary

    def cancel(self) -> None:
        self.token.cancel()

    @property
    def running(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

    # --- внутреннее ---
    def _emit_item(self, item: QueueItem) -> None:
        if self.on_item:
            try:
                self.on_item(item)
            except Exception:  # noqa: BLE001
                pass

    def _overall(self, items: list[QueueItem], current: str) -> None:
        total = len(items) or 1
        with self._lock:
            pct = sum(self._progress.get(i.id, 0.0) for i in items) / total
            finished = sum(1 for i in items if i.status not in ("pending", "running"))
        if self.on_overall:
            try:
                self.on_overall(pct, current)
            except Exception:  # noqa: BLE001
                pass
        desktop.status.update(self.script.name, finished, len(items), pct, current)

    def _run(self) -> None:
        started = time.monotonic()
        try:
            prepared = self.script.prepare_batch(self.all_items, self.values)
        except Exception as exc:  # noqa: BLE001
            log.error(f"Подготовка пакета не удалась: {exc}", self.script.name, traceback.format_exc())
            prepared = []
        items = [i for i in prepared if i.status == "pending"]
        chosen = {id(i) for i in prepared}
        for it in self.all_items:
            if id(it) not in chosen and it.status == "pending":
                it.status = "skipped"
                it.message = self.script.skip_reason(it)
                self._emit_item(it)
        self.summary.total = len(items)
        batch = Batch(self.all_items)
        log.info(f"Запуск: {len(items)} элемент(ов), потоков: {self.parallel}", self.script.name)

        def work(index: int, item: QueueItem) -> None:
            if self.token.cancelled:
                item.status = "cancelled"
                item.message = "Отменено"
                self._emit_item(item)
                return
            item.status = "running"
            item.progress = 0.0
            item.message = "Запуск…"
            item.outputs = []
            self._emit_item(item)

            last = [0.0]

            def on_progress(it: QueueItem, pct: float, msg: str) -> None:
                it.progress = pct
                if msg:
                    it.message = msg
                with self._lock:
                    self._progress[it.id] = pct
                now = time.monotonic()
                if now - last[0] >= 0.2 or pct >= 100:
                    last[0] = now
                    self._emit_item(it)
                    self._overall(items, f"{it.name}: {msg}" if msg else it.name)

            ctx = Context(self.script, item, self.values, batch, self.token, self.out_dir, index, len(items),
                          on_progress, self.ask)
            try:
                result = self.script.process(item, ctx)
            except Cancelled:
                result = Result.cancelled()
            except ToolMissing as exc:
                result = Result.failed(f"❌ {exc}")
            except Exception as exc:  # noqa: BLE001
                log.error(f"Ошибка обработки {item.name}: {exc}", self.script.name, traceback.format_exc())
                result = Result.failed(f"❌ Ошибка: {exc}")
            finally:
                ctx.cleanup()
            if self.token.cancelled and result.status not in ("done", "partial", "skipped"):
                result.status = "cancelled"
            item.status = result.status
            item.outputs = result.outputs
            item.progress = 100.0 if result.status in ("done", "partial", "skipped") else item.progress
            item.message = result.messages[-1] if result.messages else {
                "done": "Готово", "failed": "Ошибка", "skipped": "Пропущен", "cancelled": "Отменено",
                "partial": "Частично"}.get(result.status, result.status)
            with self._lock:
                self._progress[item.id] = 100.0
                self.summary.results[item.id] = result
                if result.status == "done":
                    self.summary.done += 1
                elif result.status == "partial":
                    self.summary.partial += 1
                elif result.status == "failed":
                    self.summary.failed += 1
                elif result.status == "skipped":
                    self.summary.skipped += 1
            level = {"done": "SUCCESS", "partial": "WARNING", "failed": "ERROR", "skipped": "INFO",
                     "cancelled": "WARNING"}.get(result.status, "INFO")
            for msg in result.messages:
                log.write(level if msg is result.messages[-1] else "INFO", msg, self.script.name)
            self._emit_item(item)
            if self.on_result:
                try:
                    self.on_result(item, result)
                except Exception:  # noqa: BLE001
                    pass
            self._overall(items, item.name)

        try:
            if self.parallel > 1 and len(items) > 1:
                with cf.ThreadPoolExecutor(max_workers=self.parallel) as pool:
                    futures = [pool.submit(work, i, it) for i, it in enumerate(items)]
                    for fut in futures:
                        fut.result()
            else:
                for i, it in enumerate(items):
                    work(i, it)
        finally:
            self.summary.cancelled = self.token.cancelled
            self.summary.elapsed = time.monotonic() - started
            desktop.status.finish(self.script.name, self.summary.ok, self.summary.failed, self.summary.cancelled)
            log.info(
                f"Завершено за {self.summary.elapsed:.1f} с: успешно {self.summary.ok}, ошибок {self.summary.failed}, "
                f"пропущено {self.summary.skipped}" + (" (отменено)" if self.summary.cancelled else ""),
                self.script.name,
            )
            if self.on_finish:
                try:
                    self.on_finish(self.summary)
                except Exception:  # noqa: BLE001
                    log.error("Ошибка обработчика завершения", self.script.name, traceback.format_exc())
