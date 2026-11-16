"""程序入口：Adw.Application + 一个主窗口。"""

from __future__ import annotations

import argparse
import sys

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")

from gi.repository import Adw, Gio, GLib, Gtk  # noqa: E402

from . import __version__  # noqa: E402
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
        """把窗口当前的样子渲染成 PNG 后退出。

        自己渲染而不是抓屏幕，是因为无头环境里没有窗口管理器，X 的根窗口
        上不一定留下完整的窗口内容，抓出来经常是黑屏或者上一张的残影。
        """
        window = self._window
        if window is None:
            print("截图失败：没有窗口", file=sys.stderr)
            self.quit()
            return False

        width = window.get_width() or self._width
        height = window.get_height() or self._height
        stack = getattr(window, "_stack", None)
        visible = stack.get_visible_child_name() if stack is not None else "?"
        print(
            f"截图 {self._screenshot}：请求 {self._page}，实际显示 {visible}，尺寸 {width}x{height}",
            file=sys.stderr,
        )
        print(f"图标：{window.icon_report()}", file=sys.stderr)
        if self._dump_widgets:
            for line in window.dump_tree():
                print(f"控件 {line}", file=sys.stderr)
        snapshot = Gtk.Snapshot()
        Gtk.WidgetPaintable.new(window).snapshot(snapshot, width, height)
        node = snapshot.to_node()
        renderer = window.get_renderer()
        if node is None or renderer is None:
            print("截图失败：窗口还没画出来", file=sys.stderr)
            self.quit()
            return False

        texture = renderer.render_texture(node, None)
        if texture is None:
            print("截图失败：渲染器没有返回纹理", file=sys.stderr)
            self.quit()
            return False

        texture.save_to_png(self._screenshot)
        print(f"已截图 {self._screenshot}", file=sys.stderr)
        self.quit()
        return False


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv if argv is None else argv)
    parser = argparse.ArgumentParser(
        prog="snnuautologin-desktop", description="陕师大校园网自动登录（GTK 桌面端）"
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
