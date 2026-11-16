"""协议层的离线测试。

不连真实后台，用 TCP 回环模拟一个「一次连接一次问答」的服务器，跑的是和
Unix 域套接字完全相同的帧格式。这样在 Windows 开发机上也能验证。

    python3 Linux/DesktopGtk/tests/test_client.py
"""

from __future__ import annotations

import socket
import struct
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from snnuautologin_desktop import client  # noqa: E402


class FakeServer:
    """收一帧、回一帧。回什么由 handler 决定，用来造各种畸形回包。"""

    def __init__(self, reply: bytes | None, *, close_without_reply: bool = False):
        self._reply = reply
        self._close_without_reply = close_without_reply
        self.received: dict | None = None
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", 0))
        self._sock.listen(4)
        self.port = self._sock.getsockname()[1]
        self._thread = threading.Thread(target=self._run, daemon=True)

    def __enter__(self) -> "FakeServer":
        self._thread.start()
        return self

    def __exit__(self, *exc_info) -> None:
        self._sock.close()
        self._thread.join(timeout=5)

    def connector(self, timeout: float) -> socket.socket:
        sock = socket.create_connection(("127.0.0.1", self.port), timeout=timeout)
        sock.settimeout(timeout)
        return sock

    def _run(self) -> None:
        try:
            conn, _ = self._sock.accept()
        except OSError:
            return
        with conn:
            try:
                self.received = client.decode(client.read_frame(conn))
                if self._close_without_reply:
                    return
                conn.sendall(self._reply or b"")
            except OSError:
                return


def test_round_trip_keeps_payload_untouched() -> None:
    reply = client.pack_frame(
        client.encode(
            {
                "success": True,
                "message": "设置已保存",
                "status": {
                    "state": "CaptchaRequired",
                    "effectiveRule": "SNNU",
                    "network": {"kind": "Wireless", "ssid": "SNNU", "ssidReliable": True},
                    "networkVersion": 7,
                    "awaitingCaptcha": True,
                },
                "settings": {"account": "42662134", "connectionRules": [{"id": "x", "enabled": True}]},
            }
        )
    )
    with FakeServer(reply) as server:
        result = client.send({"command": "status"}, connect=server.connector)

    assert server.received == {"command": "status"}, server.received
    assert result["success"] is True
    assert result["message"] == "设置已保存"
    # 枚举在线上是字符串，客户端不做任何映射，原样交给界面去翻译。
    assert result["status"]["state"] == "CaptchaRequired"
    assert result["status"]["network"]["ssid"] == "SNNU"


def test_frame_header_is_big_endian_length() -> None:
    payload = client.encode({"a": "中文"})
    frame = client.pack_frame(payload)
    assert frame[:4] == struct.pack(">i", len(payload))
    assert frame[4:] == payload
    # 4 字节大端，长度不会随内容膨胀成 8 字节
    assert len(frame) == len(payload) + 4


def test_large_message_round_trip() -> None:
    big = {"captcha": ["x" * 1200000]}
    try:
        client.pack_frame(client.encode(big))
    except client.AgentError:
        pass
    else:
        raise AssertionError("超过上限的消息应当被拒绝")

    body = {"command": "settings", "padding": "y" * 200000}
    reply = client.pack_frame(client.encode({"success": True}))
    with FakeServer(reply) as server:
        client.send(body, connect=server.connector)
    assert server.received is not None and len(server.received["padding"]) == 200000


def test_invalid_length_is_rejected() -> None:
    for header in (struct.pack(">i", 0), struct.pack(">i", -5), struct.pack(">i", client.MAX_MESSAGE + 1)):
        with FakeServer(header + b"{}") as server:
            try:
                client.send({"command": "status"}, connect=server.connector)
            except client.AgentError as exc:
                assert "长度" in str(exc), exc
            else:
                raise AssertionError(f"非法长度 {header!r} 应当被拒绝")


def test_closed_connection_reports_unavailable() -> None:
    with FakeServer(b"", close_without_reply=True) as server:
        try:
            client.send({"command": "status"}, connect=server.connector)
        except client.AgentUnavailable as exc:
            assert "关闭" in str(exc), exc
        else:
            raise AssertionError("对端提前断开应当抛 AgentUnavailable")


def test_malformed_json_is_reported() -> None:
    payload = b"{not json"
    with FakeServer(client.pack_frame(payload)) as server:
        try:
            client.send({"command": "status"}, connect=server.connector)
        except client.AgentError as exc:
            assert "解析" in str(exc), exc
        else:
            raise AssertionError("坏 JSON 应当抛 AgentError")


def test_missing_runtime_dir_is_reported() -> None:
    import os

    saved = os.environ.pop("XDG_RUNTIME_DIR", None)
    try:
        try:
            client.runtime_dir()
        except client.AgentUnavailable as exc:
            assert "XDG_RUNTIME_DIR" in str(exc), exc
        else:
            raise AssertionError("缺少 XDG_RUNTIME_DIR 应当抛 AgentUnavailable")
    finally:
        if saved is not None:
            os.environ["XDG_RUNTIME_DIR"] = saved


def main() -> int:
    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    failures = 0
    for test in tests:
        try:
            test()
        except AssertionError as exc:
            failures += 1
            print(f"FAIL {test.__name__}: {exc}")
        except Exception as exc:  # noqa: BLE001
            failures += 1
            print(f"ERROR {test.__name__}: {type(exc).__name__}: {exc}")
        else:
            print(f"ok   {test.__name__}")
    print(f"\n{len(tests) - failures}/{len(tests)} 通过")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
