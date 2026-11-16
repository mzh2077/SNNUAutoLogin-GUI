# SNNUAutoLogin Linux

首版目标为 Ubuntu 24.04 LTS 和 Debian 13，x86-64。桌面需要 X11 或 XWayland。命令行和桌面使用同一个当前用户后台进程。

桌面端用 GTK4 与 libadwaita 写，跟着发行版的桌面主题走，跟 KDE / GNOME 的原生控件是一套。它只依赖系统自带的 Python 与 `python3-gi`，不自带运行时，所以桌面包只有几十 KB。

## 安装与运行

安装命令行与后台服务包后，按需安装同版本的桌面包。

```bash
version=1.7.0
sudo apt install "./snnuautologin_${version}_amd64.deb"
sudo apt install "./snnuautologin-desktop_${version}_amd64.deb"
systemctl --user daemon-reload
systemctl --user start snnuautologin.service
```

桌面程序也会在打开时尝试启动已安装的用户服务。便携包解压后可直接运行 `./cli/snnuautologin agent`，另开终端运行 `./cli/snnuautologin` 或启动 `./desktop/snnuautologin-desktop`。需要登录后自启时，在便携目录先运行 `bash ./install-user-service.sh`。后台服务默认不随用户登录启动。

桌面包的运行依赖由 `apt` 自己装：`python3-gi`、`gir1.2-gtk-4.0`、`gir1.2-adw-1`、`gir1.2-pango-1.0`。中文界面需要 `fonts-noto-cjk`，它只是 Recommends，最小化安装的系统里要自己补一下。

## 命令行

```bash
snnuautologin config set account YOUR_ACCOUNT
snnuautologin config set operator campus
snnuautologin password --save
snnuautologin login
snnuautologin status
snnuautologin config set reconnect true
snnuautologin config set autostart true
```

省略 `--save` 时密码只在后台进程内存中保留。`--stdin` 可从标准输入读取一行密码。持久保存需要会话中的 Secret Service 与 `secret-tool`，密钥环无法保存时会明确提示。`snnuautologin unlock` 会重试读取密钥环。没有图形会话时可手动输入密码，后台进程重启后需重新输入。

- 验证码用 `captcha save FILE` 保存到一个新文件，查看后用 `captcha submit` 输入。`captcha refresh FILE` 生成新图。
- 规则用 `rule add wired ACCOUNT OPERATOR`、`rule add wifi ACCOUNT OPERATOR` 或 `rule add ssid ACCOUNT OPERATOR SSID` 创建。出口可选 `campus`、`mobile`、`unicom` 和 `telecom`。用 `rule list` 查看 ID，再用 `password RULE-ID --save` 为规则配置密码。
- 用 `devices` 查看列表，用 `device-offline DEVICE-ID` 下线其他设备。每次刷新生成新的设备 ID。`disconnect` 断开本机，后续行为取决于自动重连开关。
- `probe` 单独测试探测目标。`config set guard true` 启用守卫。可调整 `guard-threshold`、`probe-target`、`probe-timeout`，巡检间隔通过 `interval` 设置。
- 配置位于 `${XDG_CONFIG_HOME:-$HOME/.config}/snnuautologin/settings.json`。停止后台使用 `systemctl --user stop snnuautologin.service`。
- 安装了 NetworkManager 时可识别有线、Wi-Fi 和 SSID。无法判定接入方式时使用默认账号。桌面和命令行通过当前用户专用 socket 通信。

## 桌面端

界面代码在 `Linux/DesktopGtk/`，是一个 Python 包，靠 `python3-gi` 调 GTK4 与 libadwaita。它和命令行一样，只通过 `$XDG_RUNTIME_DIR/snnuautologin/agent.sock` 跟后台进程说话，所以换界面不用碰后台。

```bash
# 起一个假后台，没有校园网也能看界面
export XDG_RUNTIME_DIR=$(mktemp -d)
python3 Linux/DesktopGtk/tests/fake_agent.py &
PYTHONPATH=Linux/DesktopGtk python3 -m snnuautologin_desktop
```

`--page rules|devices|settings` 可以直接打开某个页面，`tests/fake_agent.py --captcha` 把状态切成等待验证码。这两个开关是给截图和人工核对用的。

## 构建与测试

在 Linux 上安装 .NET 10 SDK、`dpkg-deb` 和桌面的 GTK 依赖。共享库的 .NET 8 目标包会随 NuGet 还原。

```bash
sudo apt install python3-gi gir1.2-gtk-4.0 gir1.2-adw-1 gir1.2-pango-1.0
dotnet run --project tests/LinuxCoreTests -c Release
python3 Linux/DesktopGtk/tests/test_client.py   # 帧格式与错误分支，不需要图形环境
python3 Linux/DesktopGtk/tests/ui_smoke.py      # 窗口构造与状态渲染，不需要 GTK
bash Linux/packaging/build.sh 1.7.0
```

产物在 `artifacts/linux/`。发布前需要在两种发行版上完成桌面、密钥环、自启和安装卸载实测，并用校园网测试账号完成门户认证和设备守卫验收。
