# macOS Apple Silicon 测试版

适用于 macOS 14 或更新版本、Apple Silicon（M 系列）。应用自带 .NET 运行库。

## 安装与首次打开

打开文件名包含 macos-arm64 的 DMG，将 SNNUAutoLogin.app 拖入 Applications，弹出磁盘映像后从 Applications 启动。
这是临时签名的测试包。首次被系统拦截时，先尝试打开一次，再到「系统设置 → 隐私与安全性」选择「仍要打开」。无需关闭系统安全检查。
输入校园网账号、密码并选择出口，点击立即登录。需要图形验证码时在窗口内填写。

## 测试内容

- 核对手动登录、验证码刷新和提交、重新登录、切换出口。
- 勾选保存密码后写入 macOS 钥匙串。拒绝钥匙串访问时密码只供当前进程使用。
- 关闭窗口后应用驻留菜单栏，仍可断线重连。使用菜单栏的退出完整结束应用。
- 连接规则支持有线、任意 Wi-Fi 和指定 SSID。新增规则时申请位置权限以读取 Wi-Fi 名称。拒绝权限时按默认账号或任意 Wi-Fi 规则匹配。
- 登录项默认关闭，将应用复制到 Applications 后可在自动化页启用。系统若提示审批，请在登录项设置中允许。
- 在线设备显示门户的上线时间，支持下线其他设备和断开本机。守卫默认关闭，双地址连续三轮均失败且超出设备阈值时重连。
- 验证休眠唤醒、Wi-Fi 与有线切换、菜单栏还原窗口。检查中文输入、深浅色主题。

配置位于 ~/Library/Application Support/SNNUAutoLogin。密码不写入配置文件。若启动失败，查看该目录的 startup-error.txt。
公网 IP 由 ipinfo.io 查询，版本检查访问 GitHub。查询失败不会影响校园网认证。

## 已验证与待验证

GitHub Actions 验证共享核心模拟门户回归、ARM64 应用结构、临时签名、窗口启动、关窗后后台存活、单实例及退出。
校园网认证、位置授权、钥匙串交互、登录项与休眠唤醒需要测试者真机验证。此测试包尚未进行 Developer ID 签名和 Apple 公证。

## 构建

在 Apple Silicon Mac 安装 .NET 10 SDK 和 Xcode Command Line Tools 后运行 bash macOS/packaging/build.sh。
然后运行 bash macOS/packaging/smoke.sh artifacts/macos/SNNUAutoLogin.app 验证。
仅需下载 DMG 和 SHA256SUMS-macos-arm64.txt，应用包中附有 LICENSE 和 NOTICE。
