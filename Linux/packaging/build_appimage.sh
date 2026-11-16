#!/usr/bin/env bash
# 打一个 AppImage：后台自带，双击就能用，不用装任何包。
#
# 用法：build_appimage.sh VERSION CLI_DIR PYTHON_SRC ICON OUTPUT
#   VERSION     1.7.0~preview.1 这种；文件名里会写成 1.7.0-preview.1
#   CLI_DIR     dotnet publish 出来的自包含后台目录
#   PYTHON_SRC  Linux/DesktopGtk/snnuautologin_desktop（Python 包本身）
#   ICON        256x256 的 png
#   OUTPUT      最终 .AppImage 的完整路径
#
# 找不到 appimagetool 时只打印一句提示就退出（退出码 0），产物不生成。
# 这样在跑不了 AppImage 的机器上 build.sh 仍然能出 deb 和 tar.gz。
set -euo pipefail

version="${1:?用法：build_appimage.sh VERSION CLI_DIR PYTHON_SRC ICON OUTPUT}"
cli="${2:?缺少 CLI_DIR}"
python_src="${3:?缺少 PYTHON_SRC}"
icon="${4:?缺少 ICON}"
out="${5:?缺少 OUTPUT}"

pretty="${version//~/-}"
arch="x86_64"

find_tool() {
    if [[ -n "${APPIMAGETOOL:-}" ]]; then
        [[ -x "$APPIMAGETOOL" ]] && printf '%s\n' "$APPIMAGETOOL" && return 0
        return 1
    fi
    local name
    for name in appimagetool appimagetool-x86_64.AppImage; do
        if command -v "$name" >/dev/null 2>&1; then
            command -v "$name"
            return 0
        fi
    done
    return 1
}

if ! tool="$(find_tool)"; then
    printf '没有 appimagetool，跳过 AppImage。需要的话设 APPIMAGETOOL 指向它的可执行文件。\n' >&2
    exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
appdir="$work/AppDir"
mkdir -p "$appdir/usr/lib/snnuautologin" "$appdir/usr/share/applications" \
         "$appdir/usr/share/icons/hicolor/256x256/apps"

cp -a "$cli" "$appdir/usr/lib/snnuautologin/agent"
cp -a "$python_src" "$appdir/usr/lib/snnuautologin/desktop"
[[ -f "$appdir/usr/lib/snnuautologin/desktop/snnuautologin_desktop/client.py" ]] || {
    printf '界面包没复制进去，AppImage 打出来也是废的。\n' >&2
    exit 1
}
find "$appdir" -name '__pycache__' -type d -prune -exec rm -rf {} +

# dotnet publish 出来的 dll 带 0744（有执行位、组和他人不可读），appimagetool 会为
# 每一个都报一条「别的用户跑会 Permission denied」。这里统一收一遍权限：普通文件
# 0644，只有真需要执行的那两个留 0755。
find "$appdir" -type d -exec chmod 0755 {} +
find "$appdir" -type f -exec chmod 0644 {} +
chmod 0755 "$appdir/usr/lib/snnuautologin/agent/snnuautologin"
if [[ -e "$appdir/usr/lib/snnuautologin/agent/createdump" ]]; then
    chmod 0755 "$appdir/usr/lib/snnuautologin/agent/createdump"
fi

# 桌面文件名必须和 GTK 的 application_id 对得上，否则 GNOME 任务栏抓不到图标。
desktop_id="cn.edu.snnu.SNNUAutoLogin"
cat > "$appdir/$desktop_id.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=SNNU Campus Network
Name[zh_CN]=陕师大校园网自动登录
GenericName=Campus network login
GenericName[zh_CN]=校园网登录
Comment=Login to the SNNU campus network automatically
Comment[zh_CN]=陕师大校园网自动登录客户端
Exec=snnuautologin-desktop
Icon=$desktop_id
Terminal=false
Categories=Network;
Keywords=snnu;campus;network;校园网;
StartupWMClass=$desktop_id
DESKTOP
cp "$appdir/$desktop_id.desktop" "$appdir/usr/share/applications/"
install -m644 "$icon" "$appdir/$desktop_id.png"
install -m644 "$icon" "$appdir/usr/share/icons/hicolor/256x256/apps/$desktop_id.png"
# appimagetool 认 AppDir 根目录下这个软链
ln -s "$desktop_id.png" "$appdir/.DirIcon"

cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
# AppImage 的入口。做三件事：缺依赖时说清楚、把后台准备好、拉起界面。
set -eu

here="$(dirname "$(readlink -f "$0")")"
version="__SNNU_VERSION__"
data="${XDG_DATA_HOME:-$HOME/.local/share}/snnuautologin/appimage"

complain() {
    printf '%s\n' "$1" >&2
    # 双击启动时没有终端，能弹窗就弹一个，否则用户只会看到「没反应」
    if command -v zenity >/dev/null 2>&1; then
        zenity --error --title="陕师大校园网" --text="$1" >/dev/null 2>&1 || true
    fi
}

if ! command -v python3 >/dev/null 2>&1; then
    complain "找不到 python3。需要 python3-gi、gir1.2-gtk-4.0、gir1.2-adw-1 这几个包。"
    exit 1
fi

if ! python3 -c 'import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Gtk, Adw' >/dev/null 2>&1; then
    complain "缺 GTK4 / libadwaita 的 Python 绑定。Ubuntu 24.04 以上、Debian 13 以上、Fedora 40 以上自带；其他发行版请装 python3-gi、gir1.2-gtk-4.0、gir1.2-adw-1。"
    exit 1
fi

# 后台是自包含的 .NET 程序，不能直接从挂载点跑：AppImage 卸载之后进程会跟着挂，
# 所以先复制一份到用户目录，再从那里启动。
agent="$data/agent-$version"
if [ ! -x "$agent/snnuautologin" ]; then
    mkdir -p "$data"
    staging="$data/.staging.$$"
    rm -rf "$staging"
    cp -a "$here/usr/lib/snnuautologin/agent" "$staging"
    chmod +x "$staging/snnuautologin"
    rm -rf "$agent"
    mv "$staging" "$agent"
    # 旧版本占着一百多兆，没用了
    find "$data" -mindepth 1 -maxdepth 1 -type d -name 'agent-*' ! -name "agent-$version" \
        -exec rm -rf {} + 2>/dev/null || true
fi

if [ -z "${XDG_RUNTIME_DIR:-}" ] && [ -d "/run/user/$(id -u)" ]; then
    # 少数桌面不设这个变量，但目录是现成的
    export XDG_RUNTIME_DIR="/run/user/$(id -u)"
fi
if [ -z "${XDG_RUNTIME_DIR:-}" ]; then
    complain "缺少 XDG_RUNTIME_DIR，后台没法建套接字。请从正常的桌面会话启动，不要用 ssh。"
    exit 1
fi
runtime="$XDG_RUNTIME_DIR/snnuautologin"

# 用一次真的问答来探活：光看套接字文件在不在会误判（后台崩了文件还留着），
# 而且万一已经装了 deb 版、systemd 那边正跑着一个，这里就不该再起一个。
probe() {
    PYTHONPATH="$here/usr/lib/snnuautologin/desktop" python3 -c '
import sys
from snnuautologin_desktop import client
try:
    client.send({"command": "status"}, timeout=2.0)
except Exception as exc:
    print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
    sys.exit(1)
'
}

agent_ready() {
    probe >/dev/null 2>&1
}

if ! agent_ready; then
    mkdir -p "$runtime"
    chmod 700 "$runtime" 2>/dev/null || true
    # exec 一层子 shell，这样 $! 拿到的就是后台自己的 pid，能判断它是不是一启动就退出了
    ( cd "$data" && exec "$agent/snnuautologin" agent --autostart >>"$data/agent.log" 2>&1 </dev/null ) &
    agent_pid=$!
    tries=0
    while ! agent_ready; do
        if ! kill -0 "$agent_pid" 2>/dev/null; then
            printf '后台启动后立刻退出了，日志：\n' >&2
            tail -20 "$data/agent.log" >&2 2>/dev/null || true
            complain "后台起不来，详见 $data/agent.log"
            exit 1
        fi
        tries=$((tries + 1))
        if [ "$tries" -ge 10 ]; then
            printf '后台十秒内没应答。探活输出：\n' >&2
            probe >&2 || true
            printf '后台日志：\n' >&2
            tail -20 "$data/agent.log" >&2 2>/dev/null || true
            complain "后台没应答，详见 $data/agent.log"
            exit 1
        fi
        sleep 0.5
    done
fi

export PYTHONPATH="$here/usr/lib/snnuautologin/desktop${PYTHONPATH:+:$PYTHONPATH}"
exec python3 -m snnuautologin_desktop "$@"
APPRUN
chmod 0755 "$appdir/AppRun"
sed -i "s|__SNNU_VERSION__|$pretty|g" "$appdir/AppRun"

rm -f "$out"
# 新版 appimagetool 会要求 appstream 元数据，没有就报错，加 --no-appstream 绕开；
# 老版（AppImageKit 的 continuous）不认这个开关，所以先问一下它支不支持。
builder=("$tool")
if "$tool" --help 2>&1 | grep -q -- '--no-appstream'; then
    builder+=(--no-appstream)
fi
# 容器和 CI 里没有 FUSE，只能解压后跑
export APPIMAGE_EXTRACT_AND_RUN=1
export ARCH="$arch"

if ! "${builder[@]}" --comp zstd "$appdir" "$out" < /dev/null; then
    printf 'zstd 压缩没成，退回 appimagetool 的默认压缩再试一次。\n' >&2
    "${builder[@]}" "$appdir" "$out" < /dev/null
fi

test -s "$out"
chmod 0755 "$out"
printf '已生成 %s（%s）\n' "$out" "$(du -h "$out" | cut -f1)"
