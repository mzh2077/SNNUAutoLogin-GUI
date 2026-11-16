#!/usr/bin/env python3
"""查一个图标在这台机器上到底能不能用。

「主题里有没有这个图标」和「画不画得出来」是两回事：索引里登记了名字，
但文件缺了、或者栅格化不了，控件上只会剩一个「图标缺失」的方框。这个脚本
把这几件事分开打出来，省得对着截图猜。

    DISPLAY=:99 python3 Linux/DesktopGtk/tests/icon_probe.py computer-symbolic
    DISPLAY=:99 python3 Linux/DesktopGtk/tests/icon_probe.py /path/to/x.svg
"""

from __future__ import annotations

import os
import sys

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")

from gi.repository import Gdk, Gtk  # noqa: E402


def probe_name(theme: Gtk.IconTheme, name: str) -> None:
    print(f"{name}: has_icon={int(theme.has_icon(name))}")
    paintable = theme.lookup_icon(
        name, None, 16, 1, Gtk.TextDirection.NONE, Gtk.IconLookupFlags.FORCE_SYMBOLIC
    )
    if paintable is None:
        print("  查不到")
        return
    names = paintable.get_icon_names() if hasattr(paintable, "get_icon_names") else None
    icon_file = paintable.get_file()
    uri = icon_file.get_uri() if icon_file is not None else None
    print(f"  匹配到：{names}")
    print(f"  文件：{uri}")


def probe_file(path: str) -> None:
    if not os.path.exists(path):
        print(f"{path}: 文件不存在")
        return
    try:
        texture = Gdk.Texture.new_from_filename(path)
    except Exception as exc:  # noqa: BLE001
        print(f"{path}: 载入失败 {type(exc).__name__}: {exc}")
    else:
        print(f"{path}: 载入成功 {texture.get_width()}x{texture.get_height()}")


def main(argv: list[str]) -> int:
    args = argv[1:] or ["computer-symbolic", "view-list-symbolic"]
    display = Gdk.Display.get_default()
    if display is None:
        print("没有图形显示（DISPLAY 没设？）", file=sys.stderr)
        return 2

    theme = Gtk.IconTheme.get_for_display(display)
    print(f"主题名：{theme.get_theme_name()}")
    print(f"搜索路径：{', '.join(theme.get_search_path())}")
    for arg in args:
        if arg.endswith(".svg") or arg.endswith(".png"):
            probe_file(arg)
        else:
            probe_name(theme, arg)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
