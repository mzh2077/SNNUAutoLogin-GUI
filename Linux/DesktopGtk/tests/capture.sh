#!/usr/bin/env bash
# 逐页截图，给 CI 和本机人工核对用。
#
# 每页单独起一个 dbus 会话：Adw.Application 是单实例的，被杀掉的上一份还
# 占着总线名字，后来的进程会卡在等主实例回应上，窗口不出现，截出来就是上
# 一页的残影（或者纯黑）。
#
#   export DISPLAY=:99
#   bash Linux/DesktopGtk/tests/capture.sh artifacts/linux/shots

set -euo pipefail

out="${1:?用法：capture.sh 输出目录 [页面...]}"
shift
pages=("$@")
[ ${#pages[@]} -gt 0 ] || pages=(connection rules devices settings)

mkdir -p -- "$out"
export DISPLAY="${DISPLAY:-:99}"

for page in "${pages[@]}"; do
    PAGE="$page" OUT="$out" dbus-run-session -- bash -c '
        set -e
        snnuautologin-desktop --page "$PAGE" &
        app=$!
        sleep 6
        kill -0 "$app" || { echo "界面没有起来：$PAGE" >&2; exit 1; }
        import -window root "$OUT/ui-$PAGE.png"
        kill "$app" 2>/dev/null || true
        wait "$app" 2>/dev/null || true
    '
    echo "已截图 $out/ui-$page.png"
done
