"""假后台，用来看界面长什么样。

它不是程序的一部分，只在两种场合用：CI 里截图验证，以及本机没有校园网时
人工看界面。协议和真后台一致（同样的帧格式、同样的字段名）。

    XDG_RUNTIME_DIR=/tmp/xdg python3 Linux/DesktopGtk/tests/fake_agent.py
    XDG_RUNTIME_DIR=/tmp/xdg snnuautologin-desktop

加 --captcha 让状态变成「等待验证码」，用来看验证码那张卡片。
"""

from __future__ import annotations

import argparse
import base64
import os
import random
import socket
import struct
import sys
import uuid
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from snnuautologin_desktop import client  # noqa: E402

RULES = [
    {
        "id": "11111111-1111-1111-1111-111111111111",
        "matchType": "Wired",
        "ssid": "",
        "account": "42662134",
        "operator": "Campus",
        "enabled": True,
        "passwordRef": "",
    },
    {
        "id": "22222222-2222-2222-2222-222222222222",
        "matchType": "WirelessSsid",
        "ssid": "SNNU",
        "account": "42662134",
        "operator": "Mobile",
        "enabled": True,
        "passwordRef": "",
    },
    {
        "id": "33333333-3333-3333-3333-333333333333",
        "matchType": "WirelessAny",
        "ssid": "",
        "account": "",
        "operator": "Campus",
        "enabled": False,
        "passwordRef": "",
    },
]

SETTINGS = {
    "account": "42662134",
    "operator": "Mobile",
    "autoStart": True,
    "autoReconnect": True,
    "minimizeToTray": True,
    "keepAliveOnLock": True,
    "checkIntervalSeconds": 8,
    "deviceGuardEnabled": True,
    "deviceGuardThreshold": 2,
    "deviceGuardProbeTarget": "202.117.144.205",
    "deviceGuardExternalProbeTarget": "www.baidu.com",
    "deviceGuardProbeTimeoutSeconds": 3,
    "defaultPort": 8603,
    "connectionRules": RULES,
}

DEVICES = [
    {
        "id": "tok-1",
        "name": "Redmi K70",
        "ip": "10.138.47.91",
        "mac": "8c:aa:b5:3f:11:02",
        "loginTime": "11-16 18:20",
        "isCurrent": False,
        "canForceOffline": True,
        "status": "在线",
    },
    {
        "id": "tok-2",
        "name": "pc-workbuddy",
        "ip": "10.138.47.56",
        "mac": "a4:1b:3c:22:9e:71",
        "loginTime": "11-16 19:02",
        "isCurrent": True,
        "canForceOffline": False,
        "status": "在线",
    },
    {
        "id": "tok-3",
        "name": "iPad Air",
        "ip": "10.138.48.13",
        "mac": "1c:9e:cc:07:aa:35",
        "loginTime": "11-15 09:41",
        "isCurrent": False,
        "canForceOffline": True,
        "status": "在线",
    },
]


def _png(width: int, height: int, seed: int = 7) -> bytes:
    """画一张噪声图当验证码，省得依赖 PIL。"""
    rng = random.Random(seed)
    rows = bytearray()
    for _y in range(height):
        rows.append(0)  # filter type 0
        for _x in range(width):
            if rng.random() < 0.16:
                rows += bytes((rng.randrange(40), rng.randrange(40), rng.randrange(60)))
            else:
                rows += b"\xff\xff\xff"

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(bytes(rows), 9))
        + chunk(b"IEND", b"")
    )


class FakeAgent:
    def __init__(self, captcha: bool) -> None:
        self.captcha = captcha
        self.settings = dict(SETTINGS)
        self.version = str(uuid.uuid4())
        self.network_version = 3
        self.port = 8603
        self._captcha_png = _png(120, 40)

    def status(self) -> dict:
        state = "CaptchaRequired" if self.captcha else "Online"
        return {
            "state": state,
            "detail": "门户要求输入验证码" if self.captcha else "认证成功，出口走移动",
            "effectiveRule": "SNNU",
            "network": {
                "kind": "Wireless",
                "ssid": "SNNU",
                "ssidReliable": True,
                "interfaceDescription": "Killer Wi-Fi 6E AX1675x",
                "at": "2026-11-16T19:00:00",
            },
            "networkVersion": self.network_version,
            "port": self.port,
            "guard": {
                "state": "Idle",
                "deviceCount": 3,
                "threshold": 2,
                "statisticPort": self.port,
                "lastProbeAt": "2026-11-16T19:02:10",
                "lastProbeOk": True,
                "lastMessage": "在线设备 3 台，超过阈值 2，已处置 1 台",
            },
            "awaitingCaptcha": self.captcha,
            "canAuthenticate": True,
            "credentialStatus": "密码可用",
        }

    def dispatch(self, request: dict) -> dict:
        command = request.get("command")
        if command == "status":
            return {"success": True, "status": self.status()}
        if command == "settings":
            return {
                "success": True,
                "settings": self.settings,
                "settingsVersion": self.version,
                "status": self.status(),
            }
        if command == "save-settings":
            incoming = request.get("settings") or {}
            if isinstance(incoming, dict):
                self.settings = incoming
            self.version = str(uuid.uuid4())
            return {"success": True, "status": self.status()}
        if command == "password":
            return {"success": True, "message": "", "status": self.status()}
        if command == "login":
            self.captcha = False
            return {"success": True, "message": "已登录", "status": self.status()}
        if command == "captcha":
            return {
                "success": True,
                "captcha": base64.b64encode(self._captcha_png).decode(),
                "status": self.status(),
            }
        if command == "captcha-refresh":
            self._captcha_png = _png(120, 40, seed=random.randrange(9999))
            return {
                "success": True,
                "captcha": base64.b64encode(self._captcha_png).decode(),
                "status": self.status(),
            }
        if command == "captcha-submit":
            self.captcha = False
            return {"success": True, "message": "验证码已通过", "status": self.status()}
        if command in {"devices", "device-offline", "disconnect"}:
            return {"success": True, "devices": DEVICES, "status": self.status()}
        if command == "probe":
            return {
                "success": True,
                "message": "校内可达，校外可达",
                "probe": {"success": True, "detail": "校内可达，校外可达", "elapsedMs": 38},
                "status": self.status(),
            }
        if command in {"unlock", "guard-reconnect"}:
            return {"success": True, "status": self.status()}
        return {"success": False, "message": "未知命令"}


def main() -> int:
    parser = argparse.ArgumentParser(description="假后台，用于界面截图与人工预览")
    parser.add_argument("--captcha", action="store_true", help="状态设为等待验证码")
    parser.add_argument("--requests", type=int, default=0, help="处理多少条请求后退出，0 表示一直跑")
    args = parser.parse_args()

    runtime = client.runtime_dir()
    runtime.mkdir(parents=True, exist_ok=True)
    path = client.socket_path()
    if path.exists():
        path.unlink()

    agent = FakeAgent(captcha=args.captcha)
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(path))
    server.listen(8)
    os.chmod(path, 0o600)
    print(f"fake agent listening on {path}", flush=True)

    handled = 0
    try:
        while True:
            conn, _ = server.accept()
            with conn:
                try:
                    request = client.decode(client.read_frame(conn))
                except (client.AgentError, client.AgentUnavailable, OSError):
                    continue
                reply = agent.dispatch(request if isinstance(request, dict) else {})
                try:
                    conn.sendall(client.pack_frame(client.encode(reply)))
                except OSError:
                    continue
            handled += 1
            if args.requests and handled >= args.requests:
                break
    except KeyboardInterrupt:
        pass
    finally:
        server.close()
        if path.exists():
            path.unlink()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
