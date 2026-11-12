#!/usr/bin/env bash
set -euo pipefail
directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
target="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
file="$target/snnuautologin.service"
agent="$directory/cli/snnuautologin"
[[ -x "$agent" ]]
if [[ "$agent" == *$'\n'* || "$agent" == *$'\r'* ]]; then
    printf '安装路径不能包含换行。\n' >&2
    exit 2
fi
mkdir -p -- "$target"
# systemd expands percent specifiers and dollar variables in ExecStart.
escaped="${agent//\\/\\\\}"
escaped="${escaped//\"/\\\"}"
escaped="${escaped//%/%%}"
escaped="${escaped//\$/\$\$}"
if [[ -e "$file" ]]; then
    printf '用户服务已存在：%s\n' "$file" >&2
    exit 2
fi
(
    umask 077
    set -o noclobber
    printf '%s\n' '[Unit]' 'Description=SNNU campus network login' '' \
        '[Service]' "ExecStart=\"$escaped\" agent --autostart" 'Type=simple' \
        'Restart=on-failure' 'RestartSec=5s' 'UMask=0077' 'NoNewPrivileges=yes' '' \
        '[Install]' 'WantedBy=default.target' > "$file"
)
systemctl --user daemon-reload
printf '服务已注册。运行 systemctl --user start snnuautologin.service 即可启动。\n'
