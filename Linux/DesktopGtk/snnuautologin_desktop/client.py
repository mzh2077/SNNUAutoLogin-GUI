"""与后台进程通信的最小客户端。

后台（snnuautologin agent）监听一个 Unix 域套接字，一次连接只做一次问答：
先写 4 字节大端长度，再写 UTF-8 的 JSON；回包格式相同。

这份实现只依赖标准库，线格式与 C# 侧的 Linux/Client/AgentClient.cs 一致，
两边的默认值（重试次数、消息上限）要对得上。
"""

from __future__ import annotations

import json
import os
import socket
import struct
import time
from pathlib import Path
from typing import Any, Callable

# 单条消息上限，与 C# 侧 WriteAsync / ReadAsync 里的 1_048_576 保持一致。
MAX_MESSAGE = 1_048_576

# 后台刚被 systemctl 拉起来时套接字可能还没建好，这里给一小段重试窗口。
RETRY_ATTEMPTS = 8
RETRY_DELAY = 0.2

Connector = Callable[[float], socket.socket]


class AgentUnavailable(RuntimeError):
    """连不上后台进程。界面上要引导用户去启动 service。"""


class AgentError(RuntimeError):
    """连上了，但这次问答有问题（超时、回包损坏等）。"""


def runtime_dir() -> Path:
    base = os.environ.get("XDG_RUNTIME_DIR")
    if not base:
        raise AgentUnavailable("缺少 XDG_RUNTIME_DIR，请在正常的 Linux 用户会话运行。")
    return Path(base) / "snnuautologin"


def socket_path() -> Path:
    return runtime_dir() / "agent.sock"


def config_dir() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "snnuautologin"


def encode(message: Any) -> bytes:
    # ensure_ascii=False 只是可读性考虑，线格式不受影响；分隔符收紧一点省几个字节。
    return json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def decode(payload: bytes) -> Any:
    try:
        return json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AgentError("后台返回的内容无法解析。") from exc


def pack_frame(payload: bytes) -> bytes:
    if len(payload) > MAX_MESSAGE:
        raise AgentError("消息过大")
    return struct.pack(">i", len(payload)) + payload


def read_exactly(sock: socket.socket, size: int) -> bytes:
    buffer = bytearray()
    while len(buffer) < size:
        chunk = sock.recv(size - len(buffer))
        if not chunk:
            raise AgentUnavailable("后台进程提前关闭了连接。")
        buffer += chunk
    return bytes(buffer)


def read_frame(sock: socket.socket) -> bytes:
    (length,) = struct.unpack(">i", read_exactly(sock, 4))
    if length <= 0 or length > MAX_MESSAGE:
        raise AgentError("后台返回的消息长度无效。")
    return read_exactly(sock, length)


def connect_unix(timeout: float) -> socket.socket:
    path = str(socket_path())
    last: OSError | None = None
    for attempt in range(RETRY_ATTEMPTS):
        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        sock.settimeout(timeout)
        try:
            sock.connect(path)
            return sock
        except OSError as exc:
            last = exc
            sock.close()
            if attempt == RETRY_ATTEMPTS - 1:
                break
            time.sleep(RETRY_DELAY)
    raise AgentUnavailable(
        "后台进程未运行。请启动 snnuautologin.service，或运行 snnuautologin agent。"
    ) from last


def send(request: dict, *, timeout: float = 80.0, connect: Connector | None = None) -> dict:
    """发一条命令并等回包。超时会抛 AgentError，连不上抛 AgentUnavailable。"""

    payload = pack_frame(encode(request))
    opener = connect or connect_unix
    sock = opener(timeout)
    try:
        sock.sendall(payload)
        reply = decode(read_frame(sock))
    except socket.timeout as exc:
        raise AgentError("后台响应超时。") from exc
    finally:
        sock.close()

    if not isinstance(reply, dict):
        raise AgentError("后台返回的不是一个对象。")
    return reply
