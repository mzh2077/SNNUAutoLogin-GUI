# 陕师大校园网登录 Android 原型

连接校园 Wi-Fi 后，输入学号和密码，选择出口并点击登录。若门户要求图形验证码，界面会显示图片，支持换一张和手动提交。原型内置从上游移植的 `PortalClient.cs`，学校门户使用明文 HTTP，请仅在可信的校园网络上测试。

原型只支持前台手动登录。密码仅在当前界面中使用，不保存到磁盘。后台重连和开机启动尚未实现。

## 构建

安装 .NET 8 SDK、Android 工作负载及其对应的 Android SDK 和 JDK。解压后进入项目目录并运行：

```powershell
dotnet workload install android
dotnet build SnnuAndroid.csproj
```

如未自动找到 Android SDK 或 JDK，可向构建命令传入 `-p:AndroidSdkDirectory=...` 和 `-p:JavaSdkDirectory=...`。

GitHub Actions 的 **Android APK** 工作流会生成调试签名的 APK。打开成功的运行记录，在 **Artifacts** 下载 `SNNUAutoLogin-Android-test`，解压后安装 `SNNUAutoLogin-Android-test.apk`。同目录的 `SHA256SUMS.txt` 可用于校验。真机安装和校园网认证仍需实际验证。

