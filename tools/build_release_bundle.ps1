# ============================================================================
#  一键出包：精简 portable 目录 + Portable EXE 启动器 + 安装包
#
#  用法：
#    powershell -NoProfile -ExecutionPolicy Bypass -File tools\build_release_bundle.ps1
#    powershell ... -AssemblySuffix V1610    # 改名发布（绕开 Smart App Control 的哈希拦截）
#
#  产物（都在 artifacts\ 下）：
#    <名字>-Portable-<版本>-win-x64\          精简后的目录版
#    <名字>-Portable-<版本>-win-x64.zip       目录版压缩包
#    <名字>-Portable-<版本>.exe               单文件启动器（自解压）
#    <名字>-Setup-<版本>.exe                 安装包
# ============================================================================
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$AssemblySuffix = '',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)

$project = Join-Path $repo 'SNNUAutoLogin.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$assembly = 'SNNUAutoLogin'
if ($AssemblySuffix) { $assembly = "SNNUAutoLogin$AssemblySuffix" }

Write-Host "== 发布 $assembly $version ==" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1. publish 到临时目录
# ---------------------------------------------------------------------------
$work = Join-Path $env:TEMP ("snnu-bundle-" + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work $assembly
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$publishArgs = @($project, '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '--self-contained', '-o', $publish)
if ($AssemblySuffix) { $publishArgs += @("-p:AssemblyName=$assembly") }
dotnet publish @publishArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# ---------------------------------------------------------------------------
# 2. 裁掉运行期从不加载的模块
#
#    依据：跑完 --hosts-selftest（会走 hosts 解析这条完整路径）后，
#    tasklist /m 导出的进程实际加载模块清单里没有的文件。
#    下面每一组都单独验证过 —— 删掉后自检仍然 rc=0。
#
#    注意：Microsoft.Windows.SDK.NET.dll（23.7MB）、Microsoft.ui.xaml.dll（14.4MB）
#    虽然体积最大，但 WinRT 投影与 XAML 引擎按需加载，删了会 XamlParseException。
# ---------------------------------------------------------------------------
$trimGroups = @{
    # WinRT 投影：本程序只用 Windows.UI / Windows.Graphics / Windows.Storage.Streams，
    # 管理部署与应用通知的投影层完全没碰
    'winrt-projection' = @(
        'Microsoft.Windows.Management.Deployment.Projection.dll'
        'Microsoft.Windows.AppNotifications.Builder.Projection.dll'
        'Microsoft.Windows.AppNotifications.Projection.dll'
    )
    # System.Drawing：WinUI 3 自己有渲染管线，不走 GDI+
    'drawing' = @(
        'System.Drawing.dll'
        'System.Drawing.Primitives.dll'
    )
    # XML 序列化：配置与凭据都走 System.Text.Json
    'xml' = @(
        'System.Xml.Serialization.dll'
        'System.Xml.XDocument.dll'
        'System.Xml.ReaderWriter.dll'
        'System.Private.Xml.Linq.dll'
    )
    # ADO.NET / WCF / 邮件 / Web 监听：都用不上
    'data-net' = @(
        'System.Data.Common.dll'
        'System.Data.Common.Xml.dll'
        'System.Net.Mail.dll'
        'System.Net.HttpListener.dll'
        'System.Net.Requests.dll'
        'System.Net.WebSockets.dll'
        'System.Net.WebSockets.Client.dll'
    )
    # WCF / 分布式事务 / VisualBasic 运行时
    'wcf-tx-vb' = @(
        'System.Transactions.dll'
        'System.Transactions.Local.dll'
        'Microsoft.VisualBasic.Core.dll'
        'Microsoft.VisualBasic.dll'
        'System.Private.DataContractSerialization.dll'
        'System.Runtime.Serialization.Formatters.dll'
        'System.ServiceProcess.dll'
    )
    # 诊断 / 反射元数据 / 动态代码生成
    'diag-reflect' = @(
        'System.Reflection.Metadata.dll'
        'System.Reflection.Emit.dll'
        'System.Reflection.Emit.ILGeneration.dll'
        'System.Reflection.Primitives.dll'
        'System.Reflection.TypeExtensions.dll'
        'System.Diagnostics.Debug.dll'
        'System.Diagnostics.DiagnosticSource.dll'
        'System.Diagnostics.FileVersionInfo.dll'
        'System.Diagnostics.StackTrace.dll'
        'System.Diagnostics.TextWriterTraceListener.dll'
        'System.Diagnostics.TraceSource.dll'
    )
    # 控制台 / 数据流 / 通道
    'console-flow' = @(
        'System.Console.dll'
        'System.CommandLine.dll'
        'System.IO.Compression.ZipFile.dll'
        'System.IO.IsolatedStorage.dll'
        'System.Threading.Tasks.Dataflow.dll'
        'System.Collections.Immutable.dll'
        'System.Threading.Channels.dll'
        'System.Runtime.InteropServices.WindowsRuntime.dll'
    )
}

$exeName = "$assembly.exe"
$trimmed = 0
foreach ($g in $trimGroups.Keys) {
    $hit = 0
    foreach ($f in $trimGroups[$g]) {
        $p = Join-Path $publish $f
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; $hit++ }
    }
    if ($hit -gt 0) { Write-Host "  裁剪 $g : $hit 个文件" -ForegroundColor DarkGray }
    $trimmed += $hit
}
# 语言资源只留简体中文与英文回退
foreach ($d in @('zh-CN', 'en-us')) {
    $p = Join-Path $publish $d
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
Write-Host "  共裁掉 $trimmed 个未加载程序集" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 3. 验证裁剪后仍能跑（自检走完整代码路径，rc=0 才算过）
# ---------------------------------------------------------------------------
$selftest = Join-Path $env:TEMP 'snnu_hosts_selftest.txt'
& (Join-Path $publish $exeName) --hosts-selftest $selftest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "裁剪后自检失败（rc=$LASTEXITCODE），已中止出包" }
Write-Host "  裁剪后自检通过" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. 拷贝到 artifacts
# ---------------------------------------------------------------------------
$portableName = "$assembly-Portable-$version-win-x64"
$portableDir = Join-Path $destination $portableName
if (Test-Path -LiteralPath $portableDir) { Remove-Item -LiteralPath $portableDir -Recurse -Force }
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish '*') -Destination $portableDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $portableDir -Force

$zip = Join-Path $destination "$portableName.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -LiteralPath $portableDir -DestinationPath $zip -Force

# ---------------------------------------------------------------------------
# 5. Portable EXE 启动器（单文件，自解压到 %LOCALAPPDATA% 后启动）
#
#    为什么不做成 .NET 单文件发布：WinUI 3 + WindowsAppSDK 自包含的单文件
#    会把整个 WinAppSDK 运行时打进去，实测 535MB ~ 1.2GB，比目录版还大。
#    这里改用「壳 exe + 内嵌 zip」：壳只有几十 KB，解出来的目录版才是主体。
# ---------------------------------------------------------------------------
$launcher = Join-Path $repo 'tools\launcher\Launcher.cs'
if (Test-Path -LiteralPath $launcher) {
    $launcherExe = Join-Path $destination "$assembly-Portable-$version.exe"
    & (Join-Path $repo 'tools\launcher\build_launcher.ps1') `
        -Archive $zip -OutputExe $launcherExe -TargetExe $exeName -AppName '陕师大校园网自动登录' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }
    Write-Host "  Portable EXE: $launcherExe" -ForegroundColor Green
} else {
    Write-Host "  未找到启动器源码，跳过 Portable EXE" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 6. 安装包（Inno Setup 6）
# ---------------------------------------------------------------------------
if (!$SkipInstaller) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc "/DSrcDir=$portableDir" "/DAppExe=$exeName" "/O$destination" (Join-Path $repo 'installer\SNNUAutoLogin.iss') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }
        Write-Host "  安装包: $(Join-Path $destination "SNNUAutoLogin-Setup-$version.exe")" -ForegroundColor Green
    } else {
        Write-Host "  未找到 Inno Setup 6，跳过安装包" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 7. 校验和
# ---------------------------------------------------------------------------
$sums = Get-ChildItem -LiteralPath $destination -File |
    Where-Object { $_.Name -like "$assembly*" -and $_.Extension -in '.exe', '.zip' } |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
if ($sums) {
    [IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'), ($sums -join "`n") + "`n")
}

Write-Host ""
Write-Host "== 完成 ==" -ForegroundColor Cyan
Get-ChildItem -LiteralPath $destination -File |
    Where-Object { $_.Name -like "$assembly*" -or $_.Name -like 'SNNUAutoLogin-Setup*' } |
    ForEach-Object { "  {0,10:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name }

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
