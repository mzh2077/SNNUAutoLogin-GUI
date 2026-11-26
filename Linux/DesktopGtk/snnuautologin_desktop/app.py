"""程序入口：Adw.Application + 一个主窗口。"""

from __future__ import annotations

import argparse
import sys

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")

from gi.repository import Adw, Gio, GLib, Gtk  # noqa: E402

from . import __version__  # noqa: E402
from .snapshot import SnapshotError, render_window_png  # noqa: E402
from .window import MainWindow  # noqa: E402

APP_ID = "cn.edu.snnu.SNNUAutoLogin"
PAGES = ("connection", "rules", "devices", "settings")

# 截图前等一会儿，让首轮状态查询回来，界面不是「正在连接后台…」那个样子。
SCREENSHOT_DELAY_MS = 2500


class Application(Adw.Application):
    def __init__(
        self,
        page: str = "connection",
        screenshot: str | None = None,
        width: int = 900,
        height: int = 780,
        dump_widgets: bool = False,
    ) -> None:
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self._page = page
        self._screenshot = screenshot
        self._width = width
        self._height = height
        self._dump_widgets = dump_widgets
        self._window: MainWindow | None = None

    def do_activate(self) -> None:
        if self._window is None:
            Gtk.Window.set_default_icon_name("snnuautologin")
            self._window = MainWindow(
                application=self,
                page=self._page,
                width=self._width,
                height=self._height,
            )
        self._window.present()
        if self._screenshot:
            GLib.timeout_add(SCREENSHOT_DELAY_MS, self._save_screenshot)

    def _save_screenshot(self) -> bool:
        """把窗口当前的样子渲染成 PNG 后退出。"""
        window = self._window
        if window is None:
            print("截图失败：没有窗口", file=sys.stderr)
            self.quit()
            return False

        stack = getattr(window, "_stack", None)
        visible = stack.get_visible_child_name() if stack is not None else "?"
        print(f"截图 {self._screenshot}：请求 {self._page}，实际显示 {visible}", file=sys.stderr)
        print(f"图标：{window.icon_report()}", file=sys.stderr)
        if self._dump_widgets:
            # 只打两块：顶部标签栏（图标出问题的地方）和当前页面。
            print("== 标题栏 ==", file=sys.stderr)
            for line in window.dump_tree(window._header):
                print(f"控件 {line}", file=sys.stderr)
            print("== 页面 ==", file=sys.stderr)
            child = stack.get_visible_child() if stack is not None else None
            for line in window.dump_tree(child):
                print(f"控件 {line}", file=sys.stderr)

        try:
            width, height = render_window_png(window, self._screenshot)
        except SnapshotError as exc:
            print(f"截图失败：{exc}", file=sys.stderr)
            self.quit()
            return False

        print(f"已截图 {self._screenshot}，尺寸 {width}x{height}", file=sys.stderr)
        self.quit()
        return False


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv if argv is None else argv)
    parser = argparse.ArgumentParser(
        prog="snnuautologin-desktop", description="SNNU 校园网助手（GTK 桌面端）"
    )
    parser.add_argument("--page", choices=PAGES, default="connection", help="启动时打开哪个页面")
    parser.add_argument("--screenshot", metavar="PATH", help="把窗口渲染成 PNG 后退出，用于核对界面")
    parser.add_argument("--width", type=int, default=900, help="窗口宽度，截图时用")
    parser.add_argument("--height", type=int, default=780, help="窗口高度，截图时用")
    parser.add_argument("--dump-widgets", action="store_true", help="截图时把控件树打到日志里")
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    # GTK / Adw 自己也有命令行参数，这里只挑自己要的，其余原样交给它们去忽略。
    args, _rest = parser.parse_known_args(argv[1:])
    app = Application(
        page=args.page,
        screenshot=args.screenshot,
        width=args.width,
        height=args.height,
        dump_widgets=args.dump_widgets,
    )
    return app.run([argv[0] if argv else "snnuautologin-desktop"])
