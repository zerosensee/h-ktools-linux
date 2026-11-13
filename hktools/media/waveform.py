"""Огибающие аудио (пики по 1 мс) и автоматический подбор сдвига кросс-корреляцией."""

from __future__ import annotations

import subprocess
import threading
from array import array
from typing import Callable

from .. import tools
from .process import CancelToken, Cancelled, tracker

try:  # numpy необязателен: ускоряет расчёт и авто-синхронизацию
    import numpy as np  # type: ignore
except ImportError:  # pragma: no cover
    np = None

RATE = 8000          # частота декодирования
BIN = RATE // 1000   # сэмплов на 1 мс


class Envelope:
    """Пики модуля сигнала с разрешением 1 мс и «мипмапами» для быстрой отрисовки."""

    def __init__(self, peaks: array) -> None:
        self.levels: list[array] = [peaks]
        cur = peaks
        while len(cur) > 2048:
            nxt = array("H", (max(cur[i:i + 4]) for i in range(0, len(cur), 4)))
            self.levels.append(nxt)
            cur = nxt
        self.peak = max(peaks) if len(peaks) else 1

    @property
    def length_ms(self) -> int:
        return len(self.levels[0])

    def window_max(self, start_ms: float, end_ms: float) -> int:
        """Максимум огибающей на отрезке [start, end) в мс."""
        if end_ms <= 0 or start_ms >= self.length_ms:
            return 0
        span = max(1.0, end_ms - start_ms)
        level = 0
        while level + 1 < len(self.levels) and 4 ** (level + 1) <= span:
            level += 1
        scale = 4 ** level
        data = self.levels[level]
        a = max(0, int(start_ms // scale))
        b = min(len(data), max(a + 1, int(-(-end_ms // scale))))
        return max(data[a:b]) if b > a else 0


def compute(path: str, stream_index: int, limit_s: float | None, token: CancelToken,
            on_progress: Callable[[float], None] | None = None, duration: float = 0.0) -> Envelope:
    ffmpeg = tools.require("ffmpeg")
    cmd = [ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", f"0:{stream_index}"]
    if limit_s:
        cmd += ["-t", f"{limit_s:.3f}"]
    cmd += ["-ac", "1", "-ar", str(RATE), "-f", "s16le", "-"]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, stdin=subprocess.DEVNULL,
                            start_new_session=True)
    tracker.add(proc)
    token.attach(proc)
    peaks = array("H")
    leftover = b""
    total = (limit_s or duration) * RATE * 2 if (limit_s or duration) else 0
    read = 0
    try:
        while True:
            chunk = proc.stdout.read(RATE * 2 * 4)  # ~4 секунды
            if not chunk:
                break
            if token.cancelled:
                raise Cancelled()
            read += len(chunk)
            data = leftover + chunk
            usable = len(data) - len(data) % (BIN * 2)
            leftover = data[usable:]
            if np is not None:
                samples = np.frombuffer(data[:usable], dtype="<i2").astype(np.int32)
                pk = np.abs(samples).reshape(-1, BIN).max(axis=1)
                peaks.extend(np.minimum(pk, 65535).astype(np.uint16).tolist())
            else:
                samples = array("h")
                samples.frombytes(data[:usable])
                for i in range(0, len(samples), BIN):
                    seg = samples[i:i + BIN]
                    peaks.append(min(65535, max(max(seg), -min(seg))))
            if on_progress and total:
                on_progress(min(100.0, read / total * 100))
    finally:
        proc.wait()
        tracker.discard(proc)
        token.detach(proc)
    if token.cancelled:
        raise Cancelled()
    return Envelope(peaks)


def auto_offset(ref: Envelope, donor: Envelope, max_lag_ms: int = 15000, analyze_ms: int = 180_000,
                on_progress: Callable[[float], None] | None = None, cancel: threading.Event | None = None) -> int:
    """Сдвиг донора (мс), при котором огибающие совпадают лучше всего (+ = донор позже)."""
    step = 10  # грубый поиск с шагом 10 мс
    def pooled(env: Envelope, limit: int) -> list[float]:
        base = env.levels[0]
        n = min(len(base), limit)
        return [float(max(base[i:i + step])) for i in range(0, n, step)]

    a = pooled(ref, analyze_ms + max_lag_ms)
    b = pooled(donor, analyze_ms + max_lag_ms)
    lag_n = max_lag_ms // step
    if np is not None:
        A = np.asarray(a)
        B = np.asarray(b)
        A = (A - A.mean()) / (A.std() or 1)
        B = (B - B.mean()) / (B.std() or 1)
        best, best_lag = -1e18, 0
        for lag in range(-lag_n, lag_n + 1):
            if lag >= 0:
                x, y = A[lag:], B[:len(B)]
            else:
                x, y = A, B[-lag:]
            n = min(len(x), len(y), analyze_ms // step)
            if n < 50:
                continue
            score = float(np.dot(x[:n], y[:n]) / n)
            if score > best:
                best, best_lag = score, lag
        coarse = best_lag * step
    else:
        def norm(v: list[float]) -> list[float]:
            m = sum(v) / len(v) if v else 0
            sd = (sum((x - m) ** 2 for x in v) / len(v)) ** 0.5 if v else 1
            return [(x - m) / (sd or 1) for x in v]
        A, B = norm(a), norm(b)
        best, best_lag = -1e18, 0
        limit = min(analyze_ms, 120_000) // step
        for k, lag in enumerate(range(-lag_n, lag_n + 1)):
            if cancel is not None and cancel.is_set():
                raise Cancelled()
            x = A[lag:] if lag >= 0 else A
            y = B if lag >= 0 else B[-lag:]
            n = min(len(x), len(y), limit)
            if n < 50:
                continue
            score = sum(x[i] * y[i] for i in range(n)) / n
            if score > best:
                best, best_lag = score, lag
            if on_progress and k % 50 == 0:
                on_progress(k / (2 * lag_n + 1) * 100)
        coarse = best_lag * step
    # уточнение с шагом 1 мс в окне ±step
    ra, rb = ref.levels[0], donor.levels[0]
    n = min(len(ra), len(rb), analyze_ms)
    best, fine = -1e18, coarse
    for lag in range(coarse - step, coarse + step + 1):
        s = 0.0
        cnt = 0
        for i in range(0, n, 3):
            j = i - lag
            if 0 <= i < len(ra) and 0 <= j < len(rb):
                s += ra[i] * rb[j]
                cnt += 1
        if cnt and s / cnt > best:
            best, fine = s / cnt, lag
    # lag>0: эталон «отстаёт» → донор нужно задержать на lag
    return int(fine)
