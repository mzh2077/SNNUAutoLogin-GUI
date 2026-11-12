# SNNUAutoLogin Linux

首版目标为 Ubuntu 24.04 LTS 和 Debian 13，x86-64。桌面需要 X11 或 XWayland。命令行和桌面使用同一个当前用户后台进程。

## 安装与运行

安装命令行与后台服务包后，按需安装同版本的桌面包。

```bash
version=1.6.0
sudo apt install "./snnuautologin_${version}_amd64.deb"
sudo apt install "./snnuautologin-desktop_${version}_amd64.deb"
systemctl --user daemon-reload
systemctl --user start snnuautologin.service
```

桌面程序也会在打开时尝试启动已安装的用户服务。便携包解压后可直接运行 `./cli/snnuautologin agent`，另开终端运行 `./cli/snnuautologin` 或启动 `./desktop/snnuautologin-desktop`。需要登录后自启时，在便携目录先运行 `bash ./install-user-service.sh`。后台服务默认不随用户登录启动。

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

## 构建与测试

在 Linux 上安装 .NET 10 SDK 和 `dpkg-deb`。共享库的 .NET 8 目标包会随 NuGet 还原。执行以下命令。

```bash
dotnet run --project tests/LinuxCoreTests -c Release
bash Linux/packaging/build.sh 1.6.0
```

产物在 `artifacts/linux/`。发布前需要在两种发行版上完成桌面、密钥环、自启和安装卸载实测，并用校园网测试账号完成门户认证和设备守卫验收。
