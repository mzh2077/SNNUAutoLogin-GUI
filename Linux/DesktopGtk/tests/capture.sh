#!/usr/bin/env bash
# 逐页截图，给 CI 和人工核对用。
#
# 截图由程序自己渲染（--screenshot），不抓 X 根窗口：无头环境里没有窗口
# 管理器，抓屏经常拿到黑屏或者上一页的残影。
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
    snnuautologin-desktop --page "$page" --screenshot "$out/ui-$page.png"
done
