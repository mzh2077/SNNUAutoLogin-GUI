"""没有 GTK 的机器上也能跑的界面冒烟测试。

做法是把 gi.repository 换成一组只记录状态的替身对象，然后真的把主窗口
构造出来、灌一份设置、灌几条状态，检查渲染出来的文字和可见性对不对。

它能抓的是 Python 层面的错（名字拼错、字段名写错、状态映射错、按钮可见性
判断错），抓不到的是「长得丑不丑」。后者交给 CI 里的真机截图。

    python3 Linux/DesktopGtk/tests/ui_smoke.py
"""

from __future__ import annotations

import sys
import time
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))


# ---------------------------------------------------------------- gi 替身


class Widget:
    def __init__(self, **props):
        self.props = props
        self.children: list = []
        self.css: set[str] = set()
        self.visible = True
        self.sensitive = True

    def get_display(self):
        return DISPLAY

    def get_first_child(self):
        return self.children[0] if self.children else None

    def get_next_sibling(self):
        return None

    def append(self, child):
        self.children.append(child)

    def add(self, child):
        self.children.append(child)

    def remove(self, child):
        if child in self.children:
            self.children.remove(child)

    def add_suffix(self, child):
        self.children.append(child)

    def add_prefix(self, child):
        self.children.append(child)

    def set_child(self, child):
        self.children = [child]

    def set_visible(self, value):
        self.visible = bool(value)

    def get_visible(self):
        return self.visible

    def set_sensitive(self, value):
        self.sensitive = bool(value)

    def get_sensitive(self):
        return self.sensitive

    def add_css_class(self, name):
        self.css.add(name)

    def remove_css_class(self, name):
        self.css.discard(name)

    def has_css_class(self, name):
        return name in self.css

    def connect(self, *_args, **_kwargs):
        return 0

    def __getattr__(self, name):
        if name.startswith(("set_", "pack_", "add_", "emit_")):
            return lambda *a, **k: None
        raise AttributeError(name)


class Row(Widget):
    def __init__(self, title=None, subtitle=None, **props):
        super().__init__(**props)
        self.title = title
        self.subtitle = subtitle

    def set_title(self, value):
        self.title = value

    def get_title(self):
        return self.title

    def set_subtitle(self, value):
        self.subtitle = value

    def get_subtitle(self):
        return self.subtitle


class TextRow(Row):
    def __init__(self, title=None, text="", **props):
        super().__init__(title=title, **props)
        self.text = text

    def get_text(self):
        return self.text

    def set_text(self, value):
        self.text = value or ""


class SwitchRow(Row):
    def __init__(self, title=None, active=False, **props):
        super().__init__(title=title, **props)
        self.active = bool(active)

    def get_active(self):
        return self.active

    def set_active(self, value):
        self.active = bool(value)


class SpinRow(Row):
    def __init__(self, title=None, value=0, **props):
        super().__init__(title=title, **props)
        self.value = float(value)

    @classmethod
    def new_with_range(cls, _low, _high, _step):
        return cls()

    def get_value(self):
        return self.value

    def set_value(self, value):
        self.value = float(value)


class ComboRow(Row):
    def __init__(self, title=None, model=None, **props):
        super().__init__(title=title, **props)
        self.model = model
        self.selected = 0

    def get_selected(self):
        return self.selected

    def set_selected(self, index):
        self.selected = int(index)


class Group(Widget):
    def set_header_suffix(self, widget):
        self.suffix = widget


class ViewStack(Widget):
    def __init__(self, **props):
        super().__init__(**props)
        self.pages: dict[str, Widget] = {}
        self.icons: dict[str, str] = {}
        self._page_objects: dict[int, Page] = {}
        self.visible_name = ""

    def add_titled(self, child, name, _title):
        self.pages[name] = child
        self.append(child)

    def add_titled_with_icon(self, child, name, title, icon_name):
        self.add_titled(child, name, title)
        self.icons[name] = icon_name

    def get_page(self, child):
        if child not in self.pages.values():
            return None
        # 同一个页面每次都要拿到同一个 Page，跟真 Gtk 一样
        return self._page_objects.setdefault(id(child), Page(child))

    def get_visible_child(self):
        return self.pages.get(self.visible_name)

    def set_visible_child_name(self, name):
        assert name in self.pages, f"没有这个页面：{name}"
        self.visible_name = name

    def get_visible_child_name(self):
        return self.visible_name


class Page:
    def __init__(self, child):
        self.child = child
        self.icon_name = None

    def set_icon_name(self, name):
        self.icon_name = name


class ToastOverlay(Widget):
    def add_toast(self, toast):
        self.children.append(toast)


class Banner(Widget):
    def __init__(self, **props):
        super().__init__(**props)
        self.title = ""
        self.revealed = False

    def set_title(self, value):
        self.title = value

    def set_revealed(self, value):
        self.revealed = bool(value)


class Action(Widget):
    def __init__(self, name, *_args):
        super().__init__()
        self.name = name


class ApplicationWindow(Widget):
    def __init__(self, **props):
        super().__init__(**props)
        self.actions: dict[str, Action] = {}

    def set_content(self, child):
        self.content = child

    def add_action(self, action):
        self.actions[action.name] = action

    def get_application(self):
        return APPLICATION

    def present(self):
        pass


class Application(Widget):
    def set_accels_for_action(self, *_args):
        pass

    def quit(self):
        pass


APPLICATION = Application()


class Menu(Widget):
    def __init__(self, **props):
        super().__init__(**props)
        self.items: list[tuple[str, str]] = []

    def append(self, label, action=None):
        self.items.append((label, action or ""))


# 图标主题的替身：可用图标集合可以整个换掉，用来测「主题里没有这个图标」的分支。
DEFAULT_ICONS = {
    "network-wireless-signal-good-symbolic",
    "view-list-symbolic",
    "computer-symbolic",
    "emblem-system-symbolic",
    "edit-symbolic",
    "document-edit-symbolic",
    "media-record-symbolic",
}


class Display:
    pass


class IconTheme:
    def __init__(self):
        self.available = set(DEFAULT_ICONS)

    def has_icon(self, name):
        return name in self.available

    @classmethod
    def get_for_display(cls, _display):
        return ICON_THEME


DISPLAY = Display()
ICON_THEME = IconTheme()


class Image(Widget):
    def __init__(self, icon_name=None, **props):
        super().__init__(**props)
        self.icon_name = icon_name

    def get_icon_name(self):
        return self.icon_name

    def set_from_icon_name(self, name):
        self.icon_name = name


def _build_gi() -> types.ModuleType:
    adw = types.ModuleType("gi.repository.Adw")
    gtk = types.ModuleType("gi.repository.Gtk")
    gdk = types.ModuleType("gi.repository.Gdk")
    gio = types.ModuleType("gi.repository.Gio")
    glib = types.ModuleType("gi.repository.GLib")

    adw.ActionRow = Row
    adw.EntryRow = TextRow
    adw.PasswordEntryRow = TextRow
    adw.SwitchRow = SwitchRow
    adw.SpinRow = SpinRow
    adw.ComboRow = ComboRow
    adw.PreferencesGroup = Group
    adw.PreferencesPage = Widget
    adw.ViewStack = ViewStack
    adw.ViewSwitcher = Widget
    adw.ViewSwitcherPolicy = types.SimpleNamespace(WIDE=0, NARROW=1)
    adw.HeaderBar = Widget
    adw.ToolbarView = Widget
    adw.Banner = Banner
    adw.ToastOverlay = ToastOverlay
    adw.Toast = Widget
    adw.ApplicationWindow = ApplicationWindow
    adw.Application = Application
    adw.AboutDialog = Widget

    gtk.Box = Widget
    gtk.Image = Image
    gtk.IconTheme = IconTheme
    gtk.Picture = Widget
    gtk.Label = Widget
    gtk.Button = Widget
    gtk.Entry = TextRow
    gtk.MenuButton = Widget
    gtk.Window = Widget
    gtk.StringList = types.SimpleNamespace(new=lambda items: list(items))
    gtk.Align = types.SimpleNamespace(CENTER=0, FILL=1, START=2)
    gtk.Orientation = types.SimpleNamespace(VERTICAL=0, HORIZONTAL=1)
    gtk.ContentFit = types.SimpleNamespace(CONTAIN=0, SCALE_DOWN=1)
    gtk.License = types.SimpleNamespace(MIT_X11=0)

    gdk.Texture = types.SimpleNamespace(new_from_bytes=lambda _bytes: object())

    gio.SimpleAction = types.SimpleNamespace(new=lambda name, _p: Action(name))
    gio.Menu = Menu
    gio.ApplicationFlags = types.SimpleNamespace(DEFAULT_FLAGS=0)

    # idle_add 立刻执行，让异步链路在测试里变成确定性的；timeout 返回个假 id。
    glib.idle_add = lambda fn, *args: (fn(*args), 0)[1]
    glib.timeout_add_seconds = lambda *_args: 1
    glib.source_remove = lambda _id: True
    glib.Bytes = types.SimpleNamespace(new=lambda data: data)

    repository = types.ModuleType("gi.repository")
    for module in (adw, gtk, gdk, gio, glib):
        setattr(repository, module.__name__.split(".")[-1], module)

    gi = types.ModuleType("gi")
    gi.repository = repository
    gi.require_version = lambda *_args, **_kwargs: None
    sys.modules["gi"] = gi
    sys.modules["gi.repository"] = repository
    return gi


_build_gi()

from fake_agent import FakeAgent  # noqa: E402

from snnuautologin_desktop import client, window  # noqa: E402


def _wait(window_object, seconds: float = 5.0) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if window_object._worker.active == 0:
            return
        time.sleep(0.02)
    raise AssertionError("后台任务没有结束")


def _make_window(agent: FakeAgent, page: str = "connection"):
    client.send = lambda request, **_kwargs: agent.dispatch(request)  # type: ignore[assignment]
    win = window.MainWindow(application=APPLICATION, page=page)
    _wait(win)
    return win


def test_startup_page_argument_is_honoured() -> None:
    # 曾经因为循环变量把 page 参数覆盖掉，--page 一直不生效
    agent = FakeAgent(captcha=False)
    for page in ("connection", "rules", "devices", "settings"):
        win = _make_window(agent, page=page)
        assert win._stack.visible_name == page, f"期望 {page}，实际 {win._stack.visible_name}"


def test_every_page_has_view_switcher_icon() -> None:
    # 没有图标的页面会在 ViewSwitcher 里显示一个「图标缺失」的方框
    agent = FakeAgent(captcha=False)
    win = _make_window(agent)
    assert set(win._stack.icons) == {"connection", "rules", "devices", "settings"}, win._stack.icons
    assert all(name for name in win._stack.icons.values()), win._stack.icons


class _Theme:
    """临时换掉替身主题里的可用图标，用来测「主题里没有这个图标」的分支。"""

    def __init__(self, icons: set[str]) -> None:
        self._icons = set(icons)

    def __enter__(self):
        self._saved = set(ICON_THEME.available)
        ICON_THEME.available = set(self._icons)
        return self

    def __exit__(self, *_exc):
        ICON_THEME.available = self._saved
        return False


def test_page_icons_fall_back_to_names_the_theme_has() -> None:
    # Ubuntu 24.04 上 network-wireless-signal-good-symbolic / computer-symbolic
    # 都取不到，标签前面会掉成「图标缺失」的方框，所以要往下捡一个存在的。
    agent = FakeAgent(captcha=False)
    with _Theme({"network-wireless-symbolic", "view-list-symbolic"}):
        win = _make_window(agent)
        assert win._stack.icons.get("connection") == "network-wireless-symbolic", win._stack.icons
        assert win._stack.icons.get("rules") == "view-list-symbolic", win._stack.icons
        # 候选一个都不在主题里，就不挂图标，只显示文字——总比方框好看
        assert "devices" not in win._stack.icons, win._stack.icons
        assert "settings" not in win._stack.icons, win._stack.icons


def test_polish_icons_reapplies_every_page_icon() -> None:
    # 窗口刚建的时候图标主题索引还没铺开，有的图标会一直画成方框，
    # 所以显示出来之后要把图标名清空再写回去，逼标签重新查一次主题。
    win = _make_window(FakeAgent(captcha=False))
    win._polish_icons()
    for name, icon in win._page_icons.items():
        page = win._stack.get_page(win._page_widgets[name])
        assert page.icon_name == icon, f"{name} 最后落在 {page.icon_name}"


def test_entry_row_edit_icon_uses_the_name_the_theme_has() -> None:
    # Adw.EntryRow 行尾的小铅笔图标名写死成 edit-symbolic，主题里改名叫
    # document-edit-symbolic 的话，行尾会留一个方框。
    row = Widget()
    icon = Image(icon_name="edit-symbolic")
    row.append(icon)
    win = _make_window(FakeAgent(captcha=False))
    with _Theme({"document-edit-symbolic"}):
        win._fix_missing_icons(row)
    assert icon.get_icon_name() == "document-edit-symbolic", icon.get_icon_name()


def test_entry_row_edit_icon_is_hidden_when_there_is_no_alias() -> None:
    row = Widget()
    icon = Image(icon_name="edit-symbolic")
    row.append(icon)
    win = _make_window(FakeAgent(captcha=False))
    with _Theme(set()):
        win._fix_missing_icons(row)
    assert icon.get_visible() is False


def test_devices_page_starts_with_hint_row() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent, page="devices")
    rows = [row for row in win._device_rows if isinstance(row, Row)]
    assert rows and rows[0].get_title() == "点右上角刷新", [r.get_title() for r in rows]


def test_online_state_renders() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent)

    assert win._row_state.get_title() == "已连接", win._row_state.get_title()
    assert "认证成功" in win._row_state.get_subtitle()
    assert win._dot.has_css_class("success"), win._dot.css
    assert "无线" in win._row_network.get_subtitle()
    assert "SNNU" in win._row_network.get_subtitle()
    assert "端口 8603" in win._row_rule.get_subtitle(), win._row_rule.get_subtitle()
    assert win._row_credential.get_subtitle() == "密码可用"
    assert win._captcha_group.get_visible() is False
    assert win._login_button.get_sensitive() is True

    assert win._entry_account.get_text() == "demo-user"
    assert win._combo_operator.get_selected() == 1, "Mobile 应该选中第二项"
    assert win._switch_autostart.get_active() is True
    assert int(win._spin_interval.get_value()) == 8
    assert win._entry_target.get_text() == "202.117.144.205"


def test_rules_are_listed_with_labels() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent, page="rules")

    titles = [row.get_title() for row in win._rule_rows if isinstance(row, Row)]
    assert titles == ["有线", "无线（指定名称）", "无线（任意）"], titles
    subtitles = [row.get_subtitle() for row in win._rule_rows if isinstance(row, Row)]
    assert "SNNU" in subtitles[1] and "移动" in subtitles[1], subtitles[1]
    assert "已停用" in subtitles[2], subtitles[2]

    # 点第二条，表单应该换成它的内容
    win._select_rule(1)
    assert win._rule_ssid.get_text() == "SNNU"
    assert win._rule_type.get_selected() == 2
    assert win._rule_ssid.get_sensitive() is True

    # 切回有线时 SSID 输入框失效
    win._rule_type.set_selected(0)
    win._sync_rule_form()
    assert win._rule_ssid.get_sensitive() is False


def test_captcha_state_shows_card() -> None:
    agent = FakeAgent(captcha=True)
    win = _make_window(agent)
    _wait(win)

    assert win._captcha_group.get_visible() is True
    assert win._captcha_texture is not None, "应该已经从后台取到验证码图片"
    assert win._row_state.get_title() == "等待验证码"
    assert win._dot.has_css_class("warning")


def test_devices_render_and_offline_button_only_on_others() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent, page="devices")
    win._refresh_devices()
    _wait(win)

    rows = [row for row in win._device_rows if isinstance(row, Row)]
    assert [row.get_title() for row in rows] == ["Redmi K70", "pc-workbuddy", "iPad Air"]
    # 本机那行没有下线按钮，其他两台各有一个
    assert len(rows[1].children) == 1, "本机行只挂一个「本机」标签"
    assert len(rows[0].children) == 1 and len(rows[2].children) == 1


def test_save_account_sends_settings_then_password() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent, page="settings")

    sent: list[dict] = []
    original = agent.dispatch

    def spy(request: dict) -> dict:
        sent.append(request)
        return original(request)

    client.send = lambda request, **_kw: spy(request)  # type: ignore[assignment]

    win._entry_account.set_text("demo-updated")
    win._entry_password.set_text("secret-value")
    win._switch_save_secret.set_active(True)
    win._save_account()
    _wait(win)

    commands = [request.get("command") for request in sent]
    assert commands[:2] == ["save-settings", "password"], commands
    assert sent[1]["password"] == "secret-value" and sent[1]["save"] is True
    assert agent.settings["account"] == "demo-updated"
    # 保存后会用新的版本号重新拉一次设置
    assert "settings" in commands


def test_bad_reply_raises_banner() -> None:
    agent = FakeAgent(captcha=False)
    win = _make_window(agent)

    def boom(_request, **_kwargs):
        raise client.AgentUnavailable("后台进程未运行。")

    client.send = boom  # type: ignore[assignment]
    win._poll(force=True)
    _wait(win)
    assert win._banner.revealed is True
    assert "后台进程未运行" in win._banner.title


def main() -> int:
    tests = [(name, value) for name, value in sorted(globals().items()) if name.startswith("test_")]
    failures = 0
    for name, test in tests:
        try:
            test()
        except AssertionError as exc:
            failures += 1
            print(f"FAIL {name}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failures += 1
            print(f"ERROR {name}: {type(exc).__name__}: {exc}")
        else:
            print(f"ok   {name}")
    print(f"\n{len(tests) - failures}/{len(tests)} 通过")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
