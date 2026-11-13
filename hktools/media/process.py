"""Запуск внешних процессов: отмена, контроль дочерних процессов, построчный вывод.

Аналог ActiveProcessTracker из Windows-версии: все процессы запускаются в
собственной группе и гарантированно завершаются при отмене и выходе из приложения.
"""

from __future__ import annotations

import atexit
import os
import signal
import subprocess
import threading
from collections import deque
from dataclasses import dataclass, field
from typing import Callable, Iterable

from ..log import log


class Cancelled(Exception):
    """Операция отменена пользователем."""


class ProcessTracker:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._procs: set[subprocess.Popen] = set()

    def add(self, proc: subprocess.Popen) -> None:
        with self._lock:
            self._procs.add(proc)

    def discard(self, proc: subprocess.Popen) -> None:
        with self._lock:
            self._procs.discard(proc)

    def kill_all(self) -> None:
        with self._lock:
            procs = list(self._procs)
        for proc in procs:
            terminate(proc, grace=1.5)


tracker = ProcessTracker()
atexit.register(tracker.kill_all)


def terminate(proc: subprocess.Popen, grace: float = 3.0) -> None:
    if proc.poll() is not None:
        return
    try:
        pgid = os.getpgid(proc.pid)
    except OSError:
        pgid = None
    try:
        if pgid:
            os.killpg(pgid, signal.SIGTERM)
        else:
            proc.terminate()
        proc.wait(timeout=grace)
    except (OSError, subprocess.TimeoutExpired):
        try:
            if pgid:
                os.killpg(pgid, signal.SIGKILL)
            else:
                proc.kill()
        except OSError:
            pass


class CancelToken:
    """Флаг отмены, который умеет сразу убивать связанные процессы."""

    def __init__(self) -> None:
        self._event = threading.Event()
        self._lock = threading.Lock()
        self._procs: set[subprocess.Popen] = set()

    @property
    def cancelled(self) -> bool:
        return self._event.is_set()

    def cancel(self) -> None:
        self._event.set()
        with self._lock:
            procs = list(self._procs)
        for proc in procs:
            terminate(proc)

    def check(self) -> None:
        if self._event.is_set():
            raise Cancelled()

    def wait(self, timeout: float) -> bool:
        return self._event.wait(timeout)

    def attach(self, proc: subprocess.Popen) -> None:
        with self._lock:
            self._procs.add(proc)
        if self._event.is_set():
            terminate(proc)

    def detach(self, proc: subprocess.Popen) -> None:
        with self._lock:
            self._procs.discard(proc)


@dataclass
class ProcResult:
    returncode: int
    stdout: str = ""
    stderr_tail: list[str] = field(default_factory=list)
    cancelled: bool = False

    @property
    def ok(self) -> bool:
        return self.returncode == 0 and not self.cancelled

    def error_text(self, lines: int = 12) -> str:
        return "\n".join(self.stderr_tail[-lines:])


def _reader(stream, on_line: Callable[[str], None] | None, sink: deque | list | None, split_cr: bool) -> None:
    buf = b""
    try:
        while True:
            chunk = stream.read1(65536) if hasattr(stream, "read1") else stream.read(65536)
            if not chunk:
                break
            buf += chunk
            while True:
                idx_n = buf.find(b"\n")
                idx_r = buf.find(b"\r") if split_cr else -1
                cands = [i for i in (idx_n, idx_r) if i >= 0]
                if not cands:
                    break
                idx = min(cands)
                line = buf[:idx].decode("utf-8", "replace")
                buf = buf[idx + 1:]
                if not line.strip():
                    continue
                if sink is not None:
                    sink.append(line)
                if on_line:
                    try:
                        on_line(line)
                    except Exception:  # noqa: BLE001
                        pass
        if buf.strip():
            line = buf.decode("utf-8", "replace")
            if sink is not None:
                sink.append(line)
            if on_line:
                try:
                    on_line(line)
                except Exception:  # noqa: BLE001
                    pass
    finally:
        try:
            stream.close()
        except OSError:
            pass


def run(
    cmd: list[str],
    token: CancelToken | None = None,
    *,
    on_stdout: Callable[[str], None] | None = None,
    on_stderr: Callable[[str], None] | None = None,
    capture_stdout: bool = False,
    cwd: str | None = None,
    env: dict | None = None,
    stdin_data: bytes | None = None,
    split_cr: bool = False,
    tail: int = 60,
    source: str = "Процесс",
) -> ProcResult:
    """Запустить процесс и дождаться завершения (с поддержкой отмены)."""
    if token is not None and token.cancelled:
        return ProcResult(-1, cancelled=True)
    log.debug("$ " + " ".join(_quote(c) for c in cmd), source)
    full_env = None
    if env:
        full_env = os.environ.copy()
        full_env.update(env)
    try:
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE if stdin_data is not None else subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=cwd,
            env=full_env,
            start_new_session=True,
        )
    except OSError as exc:
        return ProcResult(127, stderr_tail=[f"Не удалось запустить {cmd[0]}: {exc}"])

    tracker.add(proc)
    if token is not None:
        token.attach(proc)
    stdout_lines: list[str] = []
    stderr_tail: deque[str] = deque(maxlen=tail)
    t_out = threading.Thread(
        target=_reader,
        args=(proc.stdout, on_stdout, stdout_lines if capture_stdout else None, split_cr),
        daemon=True,
    )
    t_err = threading.Thread(target=_reader, args=(proc.stderr, on_stderr, stderr_tail, split_cr), daemon=True)
    t_out.start()
    t_err.start()
    if stdin_data is not None:
        try:
            proc.stdin.write(stdin_data)
            proc.stdin.close()
        except OSError:
            pass
    try:
        rc = proc.wait()
    finally:
        t_out.join(timeout=5)
        t_err.join(timeout=5)
        tracker.discard(proc)
        if token is not None:
            token.detach(proc)
    cancelled = bool(token and token.cancelled)
    return ProcResult(rc, "\n".join(stdout_lines), list(stderr_tail), cancelled)


def run_pipeline(
    producer: list[str],
    consumer: list[str],
    token: CancelToken | None = None,
    *,
    on_producer_stdout_progress: Callable[[str], None] | None = None,
    on_producer_stderr: Callable[[str], None] | None = None,
    on_consumer_line: Callable[[str], None] | None = None,
    split_cr: bool = True,
    cwd: str | None = None,
    source: str = "Конвейер",
) -> ProcResult:
    """producer | consumer (например, ffmpeg → fdkaac/qaac). Прогресс читается из stderr producer."""
    if token is not None and token.cancelled:
        return ProcResult(-1, cancelled=True)
    log.debug("$ " + " ".join(_quote(c) for c in producer) + " | " + " ".join(_quote(c) for c in consumer), source)
    try:
        p1 = subprocess.Popen(producer, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              cwd=cwd, start_new_session=True)
    except OSError as exc:
        return ProcResult(127, stderr_tail=[f"Не удалось запустить {producer[0]}: {exc}"])
    try:
        p2 = subprocess.Popen(consumer, stdin=p1.stdout, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              cwd=cwd, start_new_session=True)
    except OSError as exc:
        terminate(p1)
        return ProcResult(127, stderr_tail=[f"Не удалось запустить {consumer[0]}: {exc}"])
    p1.stdout.close()  # чтобы p1 получил SIGPIPE, если p2 завершится
    for p in (p1, p2):
        tracker.add(p)
        if token is not None:
            token.attach(p)
    tail1: deque[str] = deque(maxlen=40)
    tail2: deque[str] = deque(maxlen=40)
    threads = [
        threading.Thread(target=_reader, args=(p1.stderr, on_producer_stderr, tail1, split_cr), daemon=True),
        threading.Thread(target=_reader, args=(p2.stdout, on_consumer_line, tail2, split_cr), daemon=True),
        threading.Thread(target=_reader, args=(p2.stderr, on_consumer_line, tail2, split_cr), daemon=True),
    ]
    for t in threads:
        t.start()
    rc2 = p2.wait()
    rc1 = p1.wait()
    for t in threads:
        t.join(timeout=5)
    for p in (p1, p2):
        tracker.discard(p)
        if token is not None:
            token.detach(p)
    cancelled = bool(token and token.cancelled)
    rc = rc2 if rc2 != 0 else (rc1 if rc1 not in (0, -signal.SIGPIPE, 141) else 0)
    return ProcResult(rc, "", list(tail1) + list(tail2), cancelled)


def capture(cmd: list[str], timeout: float = 60, token: CancelToken | None = None) -> tuple[int, str, str]:
    """Короткий запуск с захватом всего вывода."""
    if token is None:
        try:
            res = subprocess.run(cmd, capture_output=True, timeout=timeout, start_new_session=True)
            return res.returncode, res.stdout.decode("utf-8", "replace"), res.stderr.decode("utf-8", "replace")
        except subprocess.TimeoutExpired:
            return -9, "", "timeout"
        except OSError as exc:
            return 127, "", str(exc)
    result = run(cmd, token, capture_stdout=True, tail=400)
    return result.returncode, result.stdout, "\n".join(result.stderr_tail)


def _quote(arg: str) -> str:
    if not arg or any(c in arg for c in " \t'\"$`\\|&;<>()*?[]{}"):
        return "'" + arg.replace("'", "'\\''") + "'"
    return arg


def join_cmd(cmd: Iterable[str]) -> str:
    return " ".join(_quote(c) for c in cmd)
