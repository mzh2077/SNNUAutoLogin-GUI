#!/usr/bin/env python3
"""查一个图标名在这台机器上到底能不能用。

主题里「有没有这个图标」和「画得出来」是两回事：图标索引里登记了名字，
但对应的 SVG 文件缺了或者栅格化器（librsvg）没装，控件上就会变成一个
「图标缺失」的方框。这个脚本把两件事分开打出来。

    DISPLAY=:99 python3 Linux/DesktopGtk/tests/icon_probe.py computer-symbolic
"""

from __future__ import annotations

import sys

import gi

gi.require_version("Gtk", "4.0")

from gi.repository import Gdk, Gtk  # noqa: E402


def main(argv: list[str]) -> int:
    names = argv[1:] or ["computer-symbolic", "view-list-symbolic"]
    display = Gdk.Display.get_default()
    if display is None:
        print("没有图形显示（DISPLAY 没设？）", file=sys.stderr)
        return 2

    theme = Gtk.IconTheme.get_for_display(display)
    print(f"主题名：{theme.get_theme_name()}")
    print(f"搜索路径：{', '.join(theme.get_search_path())}")
    for name in names:
        print(f"{name}: has_icon={int(theme.has_icon(name))}")
        for size in (16, 32):
            paintable = theme.lookup_icon(
                name, None, size, 1, Gtk.TextDirection.NONE, Gtk.IconLookupFlags.FORCE_SYMBOLIC
            )
            icon_file = paintable.get_file() if paintable is not None else None
            path = icon_file.get_path() if icon_file is not None else None
            exists = icon_file.query_exists() if icon_file is not None else None
            print(f"  size={size} 文件={path} 存在={exists}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
