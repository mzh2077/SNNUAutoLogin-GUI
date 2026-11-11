# 陕师大校园网登录 Android 原型

连接校园 Wi-Fi 后，输入学号和密码，选择出口并点击登录。若门户要求图形验证码，界面会显示图片，支持换一张和手动提交。原型复用上游的 `PortalClient.cs`，学校门户使用明文 HTTP，请仅在可信的校园网络上测试。

原型只支持前台手动登录。密码仅在当前界面中使用，不保存到磁盘。后台重连和开机启动尚未实现。

## 构建

安装 .NET 8 SDK、Android 工作负载及其对应的 Android SDK 和 JDK。解压后进入项目目录并运行：

```powershell
dotnet workload install android
dotnet build SnnuAndroid.csproj
```

如未自动找到 Android SDK 或 JDK，可向构建命令传入 `-p:AndroidSdkDirectory=...` 和 `-p:JavaSdkDirectory=...`。

原型尚未编译成 APK，也没有做真机或校园网验证。
