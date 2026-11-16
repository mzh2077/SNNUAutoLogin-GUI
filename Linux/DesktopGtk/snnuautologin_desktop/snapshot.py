"""把窗口自己渲染成 PNG。

无头环境里没有窗口管理器，抓 X 的根窗口经常拿到黑屏或者上一张的残影，
所以宁可让 GTK 自己把窗口画进纹理再存盘。
"""

from __future__ import annotations

import gi

gi.require_version("Gtk", "4.0")

from gi.repository import Gtk  # noqa: E402


class SnapshotError(RuntimeError):
    pass


def render_window_png(window: Gtk.Widget, path: str) -> tuple[int, int]:
    """把窗口当前的样子存成 PNG，返回用的尺寸。"""
    width = window.get_width() or 900
    height = window.get_height() or 780
    snapshot = Gtk.Snapshot()
    Gtk.WidgetPaintable.new(window).snapshot(snapshot, width, height)
    node = snapshot.to_node()
    renderer = window.get_renderer()
    if node is None or renderer is None:
        raise SnapshotError("窗口还没画出来")
    texture = renderer.render_texture(node, None)
    if texture is None:
        raise SnapshotError("渲染器没有返回纹理")
    texture.save_to_png(path)
    return width, height
