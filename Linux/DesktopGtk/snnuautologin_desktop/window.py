"""主窗口。

界面按 GNOME 的习惯组织：顶部是 Adw.HeaderBar + ViewSwitcher，下面四个
Adw.PreferencesPage（连接 / 规则 / 设备 / 设置），所有可保存的内容都在
boxed list 里成组出现，操作按钮挂在行尾。

后端状态每 3 秒轮询一次，所有网络调用都在 Worker 的工作线程里跑。
"""

from __future__ import annotations

import base64
import copy
import subprocess
import uuid
from typing import Any, Callable

import gi

# 必须先声明版本：机器上同时装了 GTK3 和 GTK4 时，不声明会加载到旧的那个。
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")

from gi.repository import Adw, Gdk, Gio, GLib, Gtk  # noqa: E402

from . import __version__, client
from .worker import Worker

OPERATORS = ("Campus", "Mobile", "Unicom", "Telecom")
OPERATOR_LABELS = {"Campus": "校园网", "Mobile": "移动", "Unicom": "联通", "Telecom": "电信"}

MATCH_TYPES = ("Wired", "WirelessAny", "WirelessSsid")
MATCH_LABELS = {"Wired": "有线", "WirelessAny": "无线（任意）", "WirelessSsid": "无线（指定名称）"}

STATE_LABELS = {
    "Idle": "待命",
    "Checking": "正在检查",
    "Online": "已连接",
    "Offline": "未连接",
    "Connecting": "正在登录",
    "Failed": "登录失败",
    "CaptchaRequired": "等待验证码",
}
STATE_STYLE = {
    "Online": "success",
    "Offline": "error",
    "Failed": "error",
    "Checking": "warning",
    "Connecting": "warning",
    "CaptchaRequired": "warning",
}
KIND_LABELS = {"Wired": "有线", "Wireless": "无线", "Unknown": "接入方式未知"}

POLL_SECONDS = 3


def _label(mapping: dict[str, str], key: str | None) -> str:
    if not key:
        return "—"
    return mapping.get(key, key)


class MainWindow(Adw.ApplicationWindow):
    def __init__(self, page: str = "connection", **kwargs: Any) -> None:
        super().__init__(
            title="陕师大校园网自动登录",
            default_width=900,
            default_height=780,
            **kwargs,
        )
        self.set_size_request(560, 520)

        self._worker = Worker()
        self._settings: dict | None = None
        self._settings_version = ""
        self._network_version: int | None = None
        self._rule_index: int | None = None
        self._captcha_texture = None
        self._captcha_asked = False
        self._device_rows: list[Gtk.Widget] = []
        self._devices_dirty = True
        self._rule_rows: list[Gtk.Widget] = []
        self._timer = 0

        self._banner = Adw.Banner()
        self._toasts = Adw.ToastOverlay()
        self._stack = Adw.ViewStack()

        self._build_actions()
        # 每个页面都带一个图标：Adw.ViewSwitcher 会去取它，不给的话四个标签
        # 前面会各挂一个「图标缺失」的方框。
        for name, title, icon, widget in (
            ("connection", "连接", "network-wireless-signal-good-symbolic", self._build_connection_page()),
            ("rules", "规则", "view-list-symbolic", self._build_rules_page()),
            ("devices", "设备", "computer-symbolic", self._build_devices_page()),
            ("settings", "设置", "emblem-system-symbolic", self._build_settings_page()),
        ):
            if hasattr(self._stack, "add_titled_with_icon"):
                self._stack.add_titled_with_icon(widget, name, title, icon)
            else:
                self._stack.add_titled(widget, name, title)
        self._stack.connect("notify::visible-child", self._on_page_changed)

        self._toasts.set_child(self._stack)
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        body.append(self._banner)
        body.append(self._toasts)

        view = Adw.ToolbarView()
        view.add_top_bar(self._build_header())
        view.set_content(body)
        self.set_content(view)

        if page in {"connection", "rules", "devices", "settings"}:
            self._stack.set_visible_child_name(page)

        self.connect("close-request", self._on_close)
        self._timer = GLib.timeout_add_seconds(POLL_SECONDS, self._poll)
        GLib.idle_add(self._bootstrap)

    # ---------------------------------------------------------------- 骨架

    def _build_actions(self) -> None:
        for name, callback in (
            ("reload", lambda *_: self._reload_settings(announce=True)),
            ("refresh", lambda *_: self._poll(force=True)),
            ("quit", lambda *_: self.get_application().quit()),
            ("about", lambda *_: self._show_about()),
        ):
            action = Gio.SimpleAction.new(name, None)
            action.connect("activate", callback)
            self.add_action(action)

        app = self.get_application()
        if app is not None:
            app.set_accels_for_action("win.reload", ["<Control>r"])
            app.set_accels_for_action("win.quit", ["<Control>q"])

    def _build_header(self) -> Gtk.Widget:
        header = Adw.HeaderBar()
        header.set_title_widget(
            Adw.ViewSwitcher(stack=self._stack, policy=Adw.ViewSwitcherPolicy.WIDE)
        )

        menu = Gio.Menu()
        menu.append("重新加载设置", "win.reload")
        menu.append("关于", "win.about")
        menu.append("退出", "win.quit")
        header.pack_end(Gtk.MenuButton(icon_name="open-menu-symbolic", menu_model=menu, tooltip_text="菜单"))

        refresh = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="刷新状态")
        refresh.add_css_class("flat")
        refresh.connect("clicked", lambda *_: self._poll(force=True))
        header.pack_end(refresh)
        return header

    def _build_connection_page(self) -> Gtk.Widget:
        page = Adw.PreferencesPage()

        group = Adw.PreferencesGroup(title="当前状态")
        self._dot = Gtk.Image(icon_name="media-record-symbolic")
        self._dot.set_valign(Gtk.Align.CENTER)

        self._row_state = Adw.ActionRow(title="正在连接后台…", subtitle="—")
        self._row_state.add_prefix(self._dot)
        group.add(self._row_state)

        self._row_network = Adw.ActionRow(title="接入方式", subtitle="—")
        group.add(self._row_network)
        self._row_rule = Adw.ActionRow(title="生效规则", subtitle="—")
        group.add(self._row_rule)
        self._row_credential = Adw.ActionRow(title="凭据", subtitle="—")
        group.add(self._row_credential)
        page.add(group)

        actions = Adw.PreferencesGroup(title="操作")
        self._login_button = Gtk.Button(label="登录")
        self._login_button.add_css_class("suggested-action")
        self._login_button.set_valign(Gtk.Align.CENTER)
        self._login_button.connect("clicked", lambda *_: self._login())
        row = Adw.ActionRow(title="立即登录", subtitle="用「设置」里的账号与出口重新认证一次")
        row.add_suffix(self._login_button)
        actions.add(row)

        disconnect = Gtk.Button(label="断开")
        disconnect.set_valign(Gtk.Align.CENTER)
        disconnect.connect("clicked", lambda *_: self._command("disconnect", "已断开本机"))
        row = Adw.ActionRow(title="断开本机", subtitle="断开后是否重连取决于「断线自动重连」")
        row.add_suffix(disconnect)
        actions.add(row)

        probe = Gtk.Button(label="检测")
        probe.set_valign(Gtk.Align.CENTER)
        probe.connect("clicked", lambda *_: self._command("probe", "探测完成"))
        row = Adw.ActionRow(title="探测一次", subtitle="立刻测试校内与校外探测地址是否可达")
        row.add_suffix(probe)
        actions.add(row)

        unlock = Gtk.Button(label="重试")
        unlock.set_valign(Gtk.Align.CENTER)
        unlock.connect("clicked", lambda *_: self._command("unlock", "已重新读取密钥环"))
        row = Adw.ActionRow(title="重新读取密钥环", subtitle="密钥环解锁后用它把保存的密码读进来")
        row.add_suffix(unlock)
        actions.add(row)
        page.add(actions)

        self._captcha_group = Adw.PreferencesGroup(title="验证码")
        self._captcha_picture = Gtk.Picture()
        self._captcha_picture.set_size_request(132, 44)
        self._captcha_picture.set_content_fit(Gtk.ContentFit.CONTAIN)

        row = Adw.ActionRow(title="图形验证码")
        row.add_prefix(self._captcha_picture)
        self._captcha_entry = Gtk.Entry(placeholder_text="输入验证码", width_chars=10)
        self._captcha_entry.set_valign(Gtk.Align.CENTER)
        self._captcha_entry.connect("activate", lambda *_: self._submit_captcha())
        row.add_suffix(self._captcha_entry)
        submit = Gtk.Button(label="提交")
        submit.add_css_class("suggested-action")
        submit.set_valign(Gtk.Align.CENTER)
        submit.connect("clicked", lambda *_: self._submit_captcha())
        row.add_suffix(submit)
        self._captcha_group.add(row)

        refresh = Gtk.Button(label="换一张")
        refresh.set_valign(Gtk.Align.CENTER)
        refresh.connect("clicked", lambda *_: self._load_captcha(refresh=True))
        row = Adw.ActionRow(title="看不清？", subtitle="换一张会把旧图作废，需要重新输入")
        row.add_suffix(refresh)
        self._captcha_group.add(row)
        self._captcha_group.set_visible(False)
        page.add(self._captcha_group)
        return page

    def _build_rules_page(self) -> Gtk.Widget:
        page = Adw.PreferencesPage()

        group = Adw.PreferencesGroup(title="连接规则")
        hint = Adw.ActionRow(
            title="按接入方式选账号",
            subtitle="命中规则就用规则里的账号与出口；都没命中时走「设置」里的默认账号。",
        )
        group.add(hint)
        self._rule_group = group
        new = Gtk.Button(label="新建")
        new.set_valign(Gtk.Align.CENTER)
        new.connect("clicked", lambda *_: self._new_rule())
        group.set_header_suffix(new)
        page.add(group)

        edit = Adw.PreferencesGroup(title="编辑规则")
        self._rule_type = Adw.ComboRow(
            title="匹配方式",
            model=Gtk.StringList.new([MATCH_LABELS[name] for name in MATCH_TYPES]),
        )
        self._rule_type.connect("notify::selected", lambda *_: self._sync_rule_form())
        edit.add(self._rule_type)

        self._rule_ssid = Adw.EntryRow(title="Wi-Fi 名称")
        edit.add(self._rule_ssid)
        self._rule_account = Adw.EntryRow(title="账号")
        edit.add(self._rule_account)

        self._rule_operator = Adw.ComboRow(
            title="运营商出口",
            model=Gtk.StringList.new([OPERATOR_LABELS[name] for name in OPERATORS]),
        )
        edit.add(self._rule_operator)

        self._rule_password = Adw.PasswordEntryRow(title="密码（留空表示不改）")
        edit.add(self._rule_password)

        self._rule_enabled = Adw.SwitchRow(title="启用这条规则", active=True)
        edit.add(self._rule_enabled)

        save = Gtk.Button(label="保存")
        save.add_css_class("suggested-action")
        save.set_valign(Gtk.Align.CENTER)
        save.connect("clicked", lambda *_: self._save_rule())
        delete = Gtk.Button(label="删除")
        delete.add_css_class("destructive-action")
        delete.set_valign(Gtk.Align.CENTER)
        delete.connect("clicked", lambda *_: self._delete_rule())
        row = Adw.ActionRow(title="保存这条规则", subtitle="规则密码会按「保存到系统密钥环」的开关处理")
        row.add_suffix(save)
        row.add_suffix(delete)
        edit.add(row)
        page.add(edit)

        self._sync_rule_form()
        return page

    def _build_devices_page(self) -> Gtk.Widget:
        page = Adw.PreferencesPage()

        self._device_group = Adw.PreferencesGroup(title="在线设备")
        refresh = Gtk.Button(label="刷新")
        refresh.set_valign(Gtk.Align.CENTER)
        refresh.connect("clicked", lambda *_: self._refresh_devices())
        self._device_group.set_header_suffix(refresh)
        page.add(self._device_group)
        self._clear_devices()

        guard = Adw.PreferencesGroup(title="设备数守卫")
        self._row_guard = Adw.ActionRow(title="状态", subtitle="未启用")
        guard.add(self._row_guard)
        self._row_guard_count = Adw.ActionRow(title="在线设备数", subtitle="—")
        guard.add(self._row_guard_count)

        reconnect = Gtk.Button(label="重新认证")
        reconnect.set_valign(Gtk.Align.CENTER)
        reconnect.connect("clicked", lambda *_: self._command("guard-reconnect", "已重新认证"))
        row = Adw.ActionRow(title="立即按守卫处置", subtitle="设备数超阈值时主动踢掉多余在线设备")
        row.add_suffix(reconnect)
        guard.add(row)

        disconnect = Gtk.Button(label="断开本机")
        disconnect.set_valign(Gtk.Align.CENTER)
        disconnect.connect("clicked", lambda *_: self._command("disconnect", "已断开本机"))
        row = Adw.ActionRow(title="断开本机", subtitle="把当前这台机器从在线列表里摘掉")
        row.add_suffix(disconnect)
        guard.add(row)
        page.add(guard)
        return page

    def _build_settings_page(self) -> Gtk.Widget:
        page = Adw.PreferencesPage()

        account = Adw.PreferencesGroup(title="默认账号")
        self._entry_account = Adw.EntryRow(title="学号")
        account.add(self._entry_account)

        self._combo_operator = Adw.ComboRow(
            title="运营商出口",
            model=Gtk.StringList.new([OPERATOR_LABELS[name] for name in OPERATORS]),
        )
        account.add(self._combo_operator)

        self._entry_password = Adw.PasswordEntryRow(title="密码（留空表示不改）")
        account.add(self._entry_password)

        self._switch_save_secret = Adw.SwitchRow(
            title="保存到系统密钥环",
            subtitle="关掉时密码只留在后台进程内存里，重启后要重新输入",
        )
        account.add(self._switch_save_secret)

        save = Gtk.Button(label="保存")
        save.add_css_class("suggested-action")
        save.set_valign(Gtk.Align.CENTER)
        save.connect("clicked", lambda *_: self._save_account())
        row = Adw.ActionRow(title="保存账号设置")
        row.add_suffix(save)
        account.add(row)
        page.add(account)

        automation = Adw.PreferencesGroup(title="自动化")
        self._switch_autostart = Adw.SwitchRow(
            title="用户登录后自动启动", subtitle="会调用 systemctl --user enable / disable"
        )
        automation.add(self._switch_autostart)
        self._switch_reconnect = Adw.SwitchRow(title="断线自动重连")
        automation.add(self._switch_reconnect)
        self._spin_interval = Adw.SpinRow.new_with_range(3, 3600, 1)
        self._spin_interval.set_title("巡检间隔（秒）")
        automation.add(self._spin_interval)

        save = Gtk.Button(label="保存")
        save.add_css_class("suggested-action")
        save.set_valign(Gtk.Align.CENTER)
        save.connect("clicked", lambda *_: self._save_automation())
        row = Adw.ActionRow(title="保存自动化设置")
        row.add_suffix(save)
        automation.add(row)
        page.add(automation)

        guard = Adw.PreferencesGroup(title="设备数守卫")
        self._switch_guard = Adw.SwitchRow(
            title="启用设备数守卫", subtitle="在线设备超过阈值时自动处置"
        )
        guard.add(self._switch_guard)

        self._spin_threshold = Adw.SpinRow.new_with_range(0, 20, 1)
        self._spin_threshold.set_title("在线设备数阈值")
        guard.add(self._spin_threshold)

        self._entry_target = Adw.EntryRow(title="校内探测地址")
        guard.add(self._entry_target)
        self._entry_external = Adw.EntryRow(title="校外探测地址")
        guard.add(self._entry_external)

        self._spin_timeout = Adw.SpinRow.new_with_range(1, 60, 1)
        self._spin_timeout.set_title("探测超时（秒）")
        guard.add(self._spin_timeout)

        save = Gtk.Button(label="保存")
        save.add_css_class("suggested-action")
        save.set_valign(Gtk.Align.CENTER)
        save.connect("clicked", lambda *_: self._save_guard())
        row = Adw.ActionRow(title="保存守卫设置")
        row.add_suffix(save)
        guard.add(row)
        page.add(guard)
        return page

    # ------------------------------------------------------------ 后台交互

    def _on_close(self, *_args: Any) -> bool:
        if self._timer:
            GLib.source_remove(self._timer)
            self._timer = 0
        return False

    def _bootstrap(self) -> bool:
        self._reload_settings(announce=False)
        return False

    def _task_settings(self) -> dict:
        try:
            return client.send({"command": "settings"})
        except client.AgentUnavailable:
            _start_service()
            return client.send({"command": "settings"})

    def _reload_settings(self, *, announce: bool = False) -> None:
        self._worker.submit(self._task_settings, lambda reply: self._on_settings(reply, announce), self._on_error)

    def _on_settings(self, reply: dict, announce: bool = False) -> None:
        settings = reply.get("settings")
        if not isinstance(settings, dict):
            self._on_error(client.AgentError(reply.get("message") or "后台没有返回设置。"))
            return
        self._settings = settings
        self._settings_version = reply.get("settingsVersion") or ""
        self._fill_settings(settings)
        self._apply_status(reply.get("status"))
        self._banner.set_revealed(False)
        if announce:
            self._toast("设置已重新加载")

    def _fill_settings(self, settings: dict) -> None:
        self._entry_account.set_text(settings.get("account") or "")
        operator = settings.get("operator") or "Campus"
        self._combo_operator.set_selected(list(OPERATORS).index(operator) if operator in OPERATORS else 0)
        self._switch_autostart.set_active(bool(settings.get("autoStart")))
        self._switch_reconnect.set_active(bool(settings.get("autoReconnect")))
        self._spin_interval.set_value(_int(settings.get("checkIntervalSeconds"), 8))
        self._switch_guard.set_active(bool(settings.get("deviceGuardEnabled")))
        self._spin_threshold.set_value(_int(settings.get("deviceGuardThreshold"), 2))
        self._entry_target.set_text(settings.get("deviceGuardProbeTarget") or "")
        self._entry_external.set_text(settings.get("deviceGuardExternalProbeTarget") or "")
        self._spin_timeout.set_value(_int(settings.get("deviceGuardProbeTimeoutSeconds"), 3))
        self._render_rules(settings.get("connectionRules") or [])

    def _on_page_changed(self, *_args: Any) -> None:
        # 设备列表只有点进这一页才去取，第一次进来顺便自动取一次，
        # 免得看到一个空分组还得自己点刷新。
        if self._stack.get_visible_child_name() != "devices":
            return
        if self._devices_dirty and not self._worker.active:
            self._refresh_devices()

    def _poll(self, *, force: bool = False) -> bool:
        if not force and self._worker.active:
            return True
        self._worker.submit(lambda: client.send({"command": "status"}), self._on_poll, self._on_error)
        return True

    def _on_poll(self, reply: dict) -> None:
        self._banner.set_revealed(False)
        self._apply_status(reply.get("status"))
        if self._settings is None:
            self._reload_settings()

    def _on_error(self, exc: BaseException) -> None:
        message = str(exc) or type(exc).__name__
        self._banner.set_title(message)
        self._banner.set_revealed(True)

    def _toast(self, message: str) -> None:
        if message:
            self._toasts.add_toast(Adw.Toast(title=message, timeout=4))

    def _command(self, command: str, done: str) -> None:
        def task() -> dict:
            return client.send({"command": command})

        def finished(reply: dict) -> None:
            self._apply_status(reply.get("status"))
            if reply.get("success"):
                self._toast(reply.get("message") or done)
            else:
                self._banner.set_title(reply.get("message") or "操作失败")
                self._banner.set_revealed(True)

        self._worker.submit(task, finished, self._on_error)

    def _post_settings(
        self,
        mutate: Callable[[dict], None],
        done: str,
        extra: Callable[[dict], dict] | None = None,
        after: Callable[[], None] | None = None,
    ) -> None:
        """改一份设置副本发过去；extra 用来在保存成功后再补一条命令（比如存密码）。"""

        if self._settings is None:
            self._toast("还没连上后台，先等状态刷新出来")
            return

        settings = copy.deepcopy(self._settings)
        mutate(settings)
        version = self._settings_version

        def task() -> dict:
            reply = client.send(
                {"command": "save-settings", "settings": settings, "settingsVersion": version}
            )
            if reply.get("success") and extra is not None:
                follow = extra(settings)
                if follow:
                    reply = client.send(follow)
            return reply

        def finished(reply: dict) -> None:
            if not reply.get("success"):
                message = reply.get("message") or "保存失败"
                self._banner.set_title(message)
                self._banner.set_revealed(True)
                if "重新加载" in message:
                    self._reload_settings()
                return
            if after is not None:
                after()
            self._toast(done)
            self._reload_settings()

        self._worker.submit(task, finished, self._on_error)

    # ------------------------------------------------------------ 状态渲染

    def _apply_status(self, status: dict | None) -> None:
        if not isinstance(status, dict):
            return

        state = status.get("state") or "Idle"
        self._row_state.set_title(_label(STATE_LABELS, state))
        self._row_state.set_subtitle(status.get("detail") or "—")
        for name in ("success", "warning", "error", "dim-label"):
            self._dot.remove_css_class(name)
        self._dot.add_css_class(STATE_STYLE.get(state, "dim-label"))

        network = status.get("network") or {}
        kind = network.get("kind") or "Unknown"
        parts = [_label(KIND_LABELS, kind)]
        ssid = network.get("ssid") or ""
        if ssid:
            parts.append(ssid)
        elif kind == "Wireless":
            parts.append("SSID 未取到")
        if network.get("interfaceDescription"):
            parts.append(network["interfaceDescription"])
        self._row_network.set_subtitle(" · ".join(parts))

        rule = status.get("effectiveRule") or "默认账号"
        port = status.get("port")
        self._row_rule.set_subtitle(f"{rule} · 端口 {port}" if port else rule)
        self._row_credential.set_subtitle(status.get("credentialStatus") or "—")

        guard = status.get("guard") or {}
        self._row_guard.set_subtitle(guard.get("lastMessage") or "未启用")
        count = _int(guard.get("deviceCount"), 0)
        threshold = _int(guard.get("threshold"), 0)
        self._row_guard_count.set_subtitle(f"{count} 台（阈值 {threshold}）")

        self._login_button.set_sensitive(bool(status.get("canAuthenticate", True)))

        awaiting = bool(status.get("awaitingCaptcha"))
        self._captcha_group.set_visible(awaiting)
        if awaiting:
            # 只在刚进入等待状态时取一次图，否则后台一直不给图就会变成每 3 秒
            # 一次额外请求，正好是这次要避免的那种事。
            if self._captcha_texture is None and not self._captcha_asked:
                self._captcha_asked = True
                self._load_captcha()
        else:
            self._captcha_asked = False
            self._set_captcha(None)
            self._captcha_entry.set_text("")

        version = status.get("networkVersion")
        if version is not None and version != self._network_version:
            changed = self._network_version is not None
            self._network_version = version
            if changed:
                self._devices_dirty = True
                self._clear_devices()
                self._captcha_asked = False
                self._set_captcha(None)

    def _load_captcha(self, *, refresh: bool = False) -> None:
        command = "captcha-refresh" if refresh else "captcha"

        def task() -> dict:
            return client.send({"command": command})

        def finished(reply: dict) -> None:
            self._apply_status(reply.get("status"))
            data = reply.get("captcha")
            if isinstance(data, str) and data:
                try:
                    self._set_captcha(base64.b64decode(data))
                except (ValueError, TypeError):
                    self._set_captcha(None)
            elif not reply.get("success"):
                self._toast(reply.get("message") or "没有可用的验证码")
            if refresh:
                self._captcha_entry.set_text("")

        self._worker.submit(task, finished, self._on_error)

    def _set_captcha(self, data: bytes | None) -> None:
        if not data:
            self._captcha_texture = None
            self._captcha_picture.set_paintable(None)
            return
        try:
            texture = Gdk.Texture.new_from_bytes(GLib.Bytes.new(data))
        except GLib.Error:
            self._set_captcha(None)
            return
        self._captcha_texture = texture
        self._captcha_picture.set_paintable(texture)

    def _submit_captcha(self) -> None:
        code = self._captcha_entry.get_text().strip()
        if not code:
            self._toast("先输入验证码")
            return

        def task() -> dict:
            return client.send({"command": "captcha-submit", "code": code})

        def finished(reply: dict) -> None:
            self._apply_status(reply.get("status"))
            self._toast(reply.get("message") or ("验证码已提交" if reply.get("success") else "验证码不正确"))
            self._captcha_entry.set_text("")
            if self._captcha_group.get_visible():
                self._load_captcha(refresh=True)

        self._worker.submit(task, finished, self._on_error)

    # ------------------------------------------------------------ 账号动作

    def _account_dirty(self) -> bool:
        if self._settings is None:
            return False
        operator = OPERATORS[self._combo_operator.get_selected()] if self._combo_operator.get_selected() >= 0 else "Campus"
        return (
            self._entry_account.get_text().strip() != (self._settings.get("account") or "")
            or operator != (self._settings.get("operator") or "Campus")
            or bool(self._entry_password.get_text())
        )

    def _save_account(self) -> None:
        secret = self._entry_password.get_text()
        save = self._switch_save_secret.get_active()
        account = self._entry_account.get_text().strip()
        operator = OPERATORS[max(0, self._combo_operator.get_selected())]

        def mutate(settings: dict) -> None:
            settings["account"] = account
            settings["operator"] = operator

        def extra(_settings: dict) -> dict:
            if not secret:
                return {}
            return {"command": "password", "password": secret, "save": save}

        self._post_settings(
            mutate, "账号设置已保存", extra, after=lambda: self._entry_password.set_text("")
        )

    def _save_automation(self) -> None:
        autostart = self._switch_autostart.get_active()
        reconnect = self._switch_reconnect.get_active()
        interval = int(self._spin_interval.get_value())

        def mutate(settings: dict) -> None:
            settings["autoStart"] = autostart
            settings["autoReconnect"] = reconnect
            settings["checkIntervalSeconds"] = interval

        self._post_settings(mutate, "自动化设置已保存")

    def _save_guard(self) -> None:
        enabled = self._switch_guard.get_active()
        threshold = int(self._spin_threshold.get_value())
        target = self._entry_target.get_text().strip()
        external = self._entry_external.get_text().strip()
        timeout = int(self._spin_timeout.get_value())

        def mutate(settings: dict) -> None:
            settings["deviceGuardEnabled"] = enabled
            settings["deviceGuardThreshold"] = threshold
            settings["deviceGuardProbeTarget"] = target
            settings["deviceGuardExternalProbeTarget"] = external
            settings["deviceGuardProbeTimeoutSeconds"] = timeout

        self._post_settings(mutate, "守卫设置已保存")

    def _login(self) -> None:
        if self._account_dirty():
            self._save_account()

        def task() -> dict:
            return client.send({"command": "login"})

        def finished(reply: dict) -> None:
            self._apply_status(reply.get("status"))
            if reply.get("success"):
                self._toast(reply.get("message") or "已登录")
            else:
                self._banner.set_title(reply.get("message") or "登录失败")
                self._banner.set_revealed(True)

        self._worker.submit(task, finished, self._on_error)

    # ------------------------------------------------------------ 规则

    def _render_rules(self, rules: list) -> None:
        for row in self._rule_rows:
            self._rule_group.remove(row)
        self._rule_rows.clear()

        if not rules:
            row = Adw.ActionRow(title="还没有规则", subtitle="没有规则时所有接入方式都走默认账号")
            self._rule_group.add(row)
            self._rule_rows.append(row)
            self._rule_index = None
            self._fill_rule(None)
            return

        for index, rule in enumerate(rules):
            match = _label(MATCH_LABELS, rule.get("matchType"))
            account = rule.get("account") or "默认账号"
            parts = [account, _label(OPERATOR_LABELS, rule.get("operator"))]
            if rule.get("matchType") == "WirelessSsid" and rule.get("ssid"):
                parts.insert(0, rule["ssid"])
            if not rule.get("enabled", True):
                parts.append("已停用")
            row = Adw.ActionRow(title=match, subtitle=" · ".join(parts))
            row.set_activatable(True)
            row.connect("activated", lambda _row, i=index: self._select_rule(i))
            self._rule_group.add(row)
            self._rule_rows.append(row)

        if self._rule_index is None or self._rule_index >= len(rules):
            self._rule_index = None
            self._fill_rule(None)
        else:
            self._fill_rule(rules[self._rule_index])

    def _select_rule(self, index: int) -> None:
        self._rule_index = index
        rules = (self._settings or {}).get("connectionRules") or []
        if 0 <= index < len(rules):
            self._fill_rule(rules[index])

    def _fill_rule(self, rule: dict | None) -> None:
        match = (rule or {}).get("matchType") or "Wired"
        self._rule_type.set_selected(MATCH_TYPES.index(match) if match in MATCH_TYPES else 0)
        self._rule_ssid.set_text((rule or {}).get("ssid") or "")
        self._rule_account.set_text((rule or {}).get("account") or "")
        operator = (rule or {}).get("operator") or "Campus"
        self._rule_operator.set_selected(OPERATORS.index(operator) if operator in OPERATORS else 0)
        self._rule_enabled.set_active((rule or {}).get("enabled", True))
        self._rule_password.set_text("")
        self._sync_rule_form()

    def _sync_rule_form(self) -> None:
        self._rule_ssid.set_sensitive(MATCH_TYPES[max(0, self._rule_type.get_selected())] == "WirelessSsid")

    def _new_rule(self) -> None:
        self._rule_index = None
        self._fill_rule(None)

    def _save_rule(self) -> None:
        match = MATCH_TYPES[max(0, self._rule_type.get_selected())]
        operator = OPERATORS[max(0, self._rule_operator.get_selected())]
        ssid = self._rule_ssid.get_text().strip()
        account = self._rule_account.get_text().strip()
        enabled = self._rule_enabled.get_active()
        secret = self._rule_password.get_text()
        save_secret = self._switch_save_secret.get_active()
        index = self._rule_index
        rule_id = ""

        if match == "WirelessSsid" and not ssid:
            self._toast("指定 Wi-Fi 时名称不能为空")
            return

        def mutate(settings: dict) -> None:
            nonlocal rule_id
            rules = settings.setdefault("connectionRules", [])
            if index is not None and 0 <= index < len(rules):
                rule = rules[index]
            else:
                rule = {"id": str(uuid.uuid4()), "passwordRef": ""}
                rules.append(rule)
            rule["matchType"] = match
            rule["ssid"] = ssid if match == "WirelessSsid" else ""
            rule["account"] = account
            rule["operator"] = operator
            rule["enabled"] = enabled
            rule_id = rule.get("id") or ""

        def extra(_settings: dict) -> dict:
            if not secret or not rule_id:
                return {}
            return {"command": "password", "ruleId": rule_id, "password": secret, "save": save_secret}

        self._post_settings(
            mutate, "规则已保存", extra, after=lambda: self._rule_password.set_text("")
        )

    def _delete_rule(self) -> None:
        index = self._rule_index
        if index is None:
            self._toast("先在上面的列表里选一条规则")
            return

        def mutate(settings: dict) -> None:
            rules = settings.setdefault("connectionRules", [])
            if 0 <= index < len(rules):
                rules.pop(index)

        self._rule_index = None
        self._post_settings(mutate, "规则已删除")

    # ------------------------------------------------------------ 设备

    def _clear_devices(self) -> None:
        for row in self._device_rows:
            self._device_group.remove(row)
        self._device_rows.clear()
        row = Adw.ActionRow(title="点右上角刷新", subtitle="设备列表每次刷新都会重新生成，重新取一次才算最新")
        self._device_group.add(row)
        self._device_rows.append(row)

    def _refresh_devices(self) -> None:
        def task() -> dict:
            return client.send({"command": "devices"})

        def finished(reply: dict) -> None:
            self._devices_dirty = False
            self._apply_status(reply.get("status"))
            devices = reply.get("devices") or []
            for row in self._device_rows:
                self._device_group.remove(row)
            self._device_rows.clear()

            if not devices:
                row = Adw.ActionRow(title="没有取到设备", subtitle=reply.get("message") or "稍后再试一次")
                self._device_group.add(row)
                self._device_rows.append(row)
                return

            for device in devices:
                row = self._device_row(device)
                self._device_group.add(row)
                self._device_rows.append(row)

            if not reply.get("success"):
                self._banner.set_title(reply.get("message") or "设备列表可能不是最新的")
                self._banner.set_revealed(True)

        self._worker.submit(task, finished, self._on_error)

    def _device_row(self, device: dict) -> Gtk.Widget:
        parts = [device.get("ip") or "—", device.get("mac") or "—"]
        if device.get("loginTime"):
            parts.append(f"登录 {device['loginTime']}")
        if device.get("status"):
            parts.append(device["status"])
        row = Adw.ActionRow(title=device.get("name") or "未知设备", subtitle=" · ".join(parts))

        if device.get("isCurrent"):
            tag = Gtk.Label(label="本机", valign=Gtk.Align.CENTER)
            tag.add_css_class("dim-label")
            row.add_suffix(tag)
        elif device.get("canForceOffline"):
            token = device.get("id") or ""
            button = Gtk.Button(label="下线", valign=Gtk.Align.CENTER)
            button.add_css_class("destructive-action")
            button.connect("clicked", lambda *_, token=token: self._offline(token))
            row.add_suffix(button)
        return row

    def _offline(self, token: str) -> None:
        def task() -> dict:
            return client.send({"command": "device-offline", "device": token})

        def finished(reply: dict) -> None:
            self._apply_status(reply.get("status"))
            if reply.get("success"):
                self._toast("已把该设备下线")
            else:
                self._banner.set_title(reply.get("message") or "下线失败，先刷新列表")
                self._banner.set_revealed(True)
            self._refresh_devices()

        self._worker.submit(task, finished, self._on_error)

    # ------------------------------------------------------------ 关于

    def _show_about(self) -> None:
        fields = dict(
            application_name="陕师大校园网自动登录",
            application_icon="snnuautologin",
            version=__version__,
            developer_name="SNNUAutoLogin contributors",
            comments="校园网门户自动认证、连接规则与设备数守卫的桌面客户端。",
            website="https://github.com/mzh2077/SNNUAutoLogin-GUI",
            issue_url="https://github.com/mzh2077/SNNUAutoLogin-GUI/issues",
            license_type=Gtk.License.MIT_X11,
        )
        # 1.5 起叫 AboutDialog，之前的版本叫 AboutWindow，两个都留着以防发行版太老。
        if hasattr(Adw, "AboutDialog"):
            Adw.AboutDialog(**fields).present(self)
        else:
            Adw.AboutWindow(transient_for=self, **fields).present()


def _int(value: Any, fallback: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return fallback


def _start_service() -> None:
    """后台没起来时尝试拉起用户服务，和 Windows 版打开窗口时的行为一致。"""
    try:
        subprocess.run(
            ["systemctl", "--user", "start", "snnuautologin.service"],
            timeout=10,
            capture_output=True,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        pass
