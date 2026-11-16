"""程序入口：Adw.Application + 一个主窗口。"""

from __future__ import annotations

import argparse
import sys

from gi.repository import Adw, Gio, Gtk

from . import __version__
from .window import MainWindow

APP_ID = "cn.edu.snnu.SNNUAutoLogin"
PAGES = ("connection", "rules", "devices", "settings")


class Application(Adw.Application):
    def __init__(self, page: str = "connection") -> None:
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self._page = page
        self._window: MainWindow | None = None

    def do_activate(self) -> None:
        if self._window is None:
            Gtk.Window.set_default_icon_name("snnuautologin")
            self._window = MainWindow(application=self, page=self._page)
        self._window.present()


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv if argv is None else argv)
    parser = argparse.ArgumentParser(
        prog="snnuautologin-desktop", description="陕师大校园网自动登录（GTK 桌面端）"
    )
    parser.add_argument("--page", choices=PAGES, default="connection", help="启动时打开哪个页面")
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    # GTK / Adw 自己也有命令行参数，这里只挑自己要的，其余原样交给它们去忽略。
    args, _rest = parser.parse_known_args(argv[1:])
    return Application(page=args.page).run([argv[0] if argv else "snnuautologin-desktop"])
