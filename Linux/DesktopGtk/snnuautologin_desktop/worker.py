"""把阻塞的套接字调用挪到工作线程，结果回到 GTK 主循环。

GTK 不是线程安全的，任何控件操作都必须在主循环里做，所以这里只负责
「后台跑、主循环回」。队列是串行的，同一时刻只有一个请求在飞。
"""

from __future__ import annotations

import traceback
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable

from gi.repository import GLib


class Worker:
    def __init__(self) -> None:
        self._pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="agent")
        self._active = 0

    @property
    def active(self) -> int:
        """在飞或排队的请求数，界面用它挡住重复点击。"""
        return self._active

    def submit(
        self,
        task: Callable[[], Any],
        on_done: Callable[[Any], None] | None = None,
        on_error: Callable[[BaseException], None] | None = None,
    ) -> None:
        self._active += 1

        def run() -> None:
            try:
                result = task()
            except BaseException as exc:  # noqa: BLE001 - 交给界面展示
                GLib.idle_add(self._deliver, on_error, exc)
            else:
                GLib.idle_add(self._deliver, on_done, result)

        self._pool.submit(run)

    def _deliver(self, callback: Callable[[Any], None] | None, value: Any) -> bool:
        # 先减计数，回调里还能继续发新请求。
        self._active = max(0, self._active - 1)
        if callback is None:
            if isinstance(value, BaseException):
                traceback.print_exception(type(value), value, value.__traceback__)
            return False
        try:
            callback(value)
        except Exception:  # noqa: BLE001 - 回调里的异常不能拖垮主循环
            traceback.print_exc()
        return False
