## 改了什么

<!-- 一两句话说清楚这个 PR 做了什么，涉及界面的贴张截图 -->

## 为什么

<!-- 原来的行为是什么、问题出在哪。如果有关联 issue，写 Fixes #123 -->

## 验证

- [ ] `dotnet build -c Debug -p:Platform=x64` 无新增警告
- [ ] `dotnet run --project tests/StartupTests -c Release` 全过
- [ ] `dotnet run --project tests/CredentialTests -c Release` 全过
- [ ] 改了界面：跑过 `--snapshot` 与 `--ui-test`，布局正常

## 其他

<!-- 版本号是否两处同步改过、是否影响已安装用户的配置或启动项 -->
