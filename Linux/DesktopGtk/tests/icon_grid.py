#!/usr/bin/env python3
"""把一串候选图标名画成一张对照表，看哪些在这台机器上真能画出来。

给标签页挑图标时踩过坑：图标名能查到、SVG 也能解码，可放在
Adw.ViewSwitcherButton 里就是「图标缺失」的方框。这个脚本把候选都摆出来
截一张图，谁画得出来一眼就能看出来。

    DISPLAY=:99 python3 Linux/DesktopGtk/tests/icon_grid.py /tmp/icons.png
"""

from __future__ import annotations

import sys
from pathlib import Path

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")

from gi.repository import Adw, GLib, Gtk  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from snnuautologin_desktop.snapshot import SnapshotError, render_window_png  # noqa: E402

# 前两个是已知能画出来的对照组，最后一个是「缺失图标的方框」长什么样。
CANDIDATES = (
    "view-list-symbolic",
    "emblem-system-symbolic",
    "computer-symbolic",
    "network-wireless-signal-good-symbolic",
    "network-wireless-symbolic",
    "network-workgroup-symbolic",
    "network-transmit-receive-symbolic",
    "network-server-symbolic",
    "network-wired-symbolic",
    "video-display-symbolic",
    "device-desktop-symbolic",
    "smartphone-symbolic",
    "preferences-system-symbolic",
    "document-edit-symbolic",
    "edit-symbolic",
    "image-missing-symbolic",
)


class Grid(Adw.ApplicationWindow):
    def __init__(self, target: str) -> None:
        super().__init__(title="图标对照表", default_width=760, default_height=340)
        self._target = target
        self._box = Gtk.FlowBox(
            max_children_per_line=4,
            min_children_per_line=4,
            row_spacing=12,
            column_spacing=12,
            margin_top=18,
            margin_bottom=18,
            margin_start=18,
            margin_end=18,
            selection_mode=Gtk.SelectionMode.NONE,
        )
        for name in CANDIDATES:
            cell = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
            cell.append(Gtk.Image(icon_name=name, pixel_size=24))
            cell.append(Gtk.Label(label=name.replace("-symbolic", ""), wrap=True, max_width_chars=18))
            self._box.append(cell)
        self.set_content(self._box)

    def save(self) -> bool:
        try:
            width, height = render_window_png(self, self._target)
        except SnapshotError as exc:
            print(f"截图失败：{exc}", file=sys.stderr)
        else:
            print(f"已截图 {self._target}，尺寸 {width}x{height}", file=sys.stderr)
        self.get_application().quit()
        return False


class Application(Adw.Application):
    def __init__(self, target: str) -> None:
        super().__init__(application_id="cn.edu.snnu.SNNUAutoLogin.IconGrid")
        self._target = target
        self._window: Grid | None = None

    def do_activate(self) -> None:
        if self._window is None:
            self._window = Grid(self._target)
        self._window.present()
        GLib.timeout_add(1200, self._window.save)


def main(argv: list[str]) -> int:
    target = argv[1] if len(argv) > 1 else "/tmp/icons.png"
    return Application(target).run(argv[:1])


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
