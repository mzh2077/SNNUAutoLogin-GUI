param([Parameter(Mandatory)][string]$Installer, [string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run installer smoke tests only on a disposable GitHub Actions runner.' }
if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'SNNUAutoLogin')) { throw 'Existing user data found; refusing installer smoke test.' }
# 版本号从 csproj 读，避免每次改版本都要同步改这里
if (!$ExpectedVersion) {
    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\SNNUAutoLogin.csproj')
    $ExpectedVersion = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$ExpectedFileVersion = "$ExpectedVersion.0"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -LiteralPath $runKey -Name 'SNNUAutoLogin' -ErrorAction SilentlyContinue) { throw 'Existing startup entry found; refusing installer smoke test.' }
$testDir = Join-Path $env:TEMP ('SNNU-InstallerTest-' + [Guid]::NewGuid().ToString('N'))
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SNNU 校园网助手.lnk'
if (Test-Path -LiteralPath $desktopLink) { throw 'Existing desktop shortcut found; refusing installer smoke test.' }
$installLog = "$testDir-install.log"
$proc = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=desktopicon', "/DIR=`"$testDir`"", "/LOG=`"$installLog`"") -PassThru -WindowStyle Hidden
if (!$proc.WaitForExit(120000)) { throw 'Installer timed out' }
if ($proc.ExitCode -ne 0) { throw "Installer failed ($($proc.ExitCode)): $installLog" }
$exe = Join-Path $testDir 'SNNUAutoLogin.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Installed executable missing' }
$installedVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($installedVersion -ne $ExpectedFileVersion) { throw "Installed version mismatch: $installedVersion != $ExpectedFileVersion" }
$shell = New-Object -ComObject WScript.Shell
function Assert-ShortcutIcon([string]$path) {
    $link = $shell.CreateShortcut($path)
    $iconPath = $link.IconLocation -replace ',0$', ''
    if ($link.TargetPath -ne $exe -or !(Test-Path -LiteralPath $iconPath) -or
        [IO.Path]::GetFileName($iconPath) -notmatch '^app-[0-9a-fA-F]{64}\.ico$') {
        Write-Host "Shortcut: $path; target: [$($link.TargetPath)]; expected target: [$exe]; icon: [$($link.IconLocation)]"
        Get-ChildItem -LiteralPath (Join-Path $testDir 'Assets') -Filter '*.ico' | Select-Object Name, Length | Out-String | Write-Host
        Get-Content -LiteralPath $installLog | Select-Object -Last 65 | Write-Host
        throw "Shortcut icon mismatch: $path"
    }
    if ((Get-FileHash -LiteralPath $iconPath).Hash -ne (Get-FileHash -LiteralPath (Join-Path $testDir 'Assets/app.ico')).Hash) {
        throw 'Shortcut icon content differs from the application icon'
    }
}
Assert-ShortcutIcon $desktopLink
# Simulate old desktop/pinned link metadata without programmatically pinning an app.
$link = $shell.CreateShortcut($desktopLink)
$link.IconLocation = "$env:WINDIR\System32\shell32.dll,0"
$link.Save()
$pinDir = Join-Path $env:APPDATA 'Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar'
New-Item -ItemType Directory -Force $pinDir | Out-Null
$pin = Join-Path $pinDir ('SNNU-test-' + [Guid]::NewGuid().ToString('N') + '.lnk')
$otherPin = "$pin-other.lnk"
$link = $shell.CreateShortcut($pin)
$link.TargetPath = $exe
$link.IconLocation = "$env:WINDIR\System32\shell32.dll,0"
$link.Save()
$link = $shell.CreateShortcut($otherPin)
$link.TargetPath = "$env:WINDIR\System32\cmd.exe"
$link.IconLocation = "$env:WINDIR\System32\shell32.dll,0"
$link.Save()
$otherHash = (Get-FileHash -LiteralPath $otherPin).Hash
$upgrade = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=', "/DIR=`"$testDir`"") -PassThru -WindowStyle Hidden
if (!$upgrade.WaitForExit(120000) -or $upgrade.ExitCode -ne 0) { throw 'Upgrade installation failed' }
Assert-ShortcutIcon $desktopLink
Assert-ShortcutIcon $pin
if ((Get-FileHash -LiteralPath $otherPin).Hash -ne $otherHash) { throw 'Upgrade modified an unrelated shortcut' }
Remove-Item -LiteralPath $pin, $otherPin
Write-Host 'Desktop and matching pinned shortcut icon upgrade checks passed.'
# No campus credentials are configured on the disposable CI runner.
$app = Start-Process -FilePath $exe -ArgumentList '--test-exit' -WorkingDirectory $env:TEMP -PassThru -WindowStyle Hidden
if (!$app.WaitForExit(30000)) {
    $app.Kill()
    $hookError = Join-Path $env:TEMP 'snnu_hook_error.txt'
    if (Test-Path -LiteralPath $hookError) { Get-Content -LiteralPath $hookError | Write-Host }
    throw 'Installed application exit self-test timed out'
}
if ($app.ExitCode -ne 0) { throw "Installed application failed ($($app.ExitCode))" }
$exitReport = Join-Path $env:TEMP 'snnu_exit.txt'
if (!(Test-Path -LiteralPath $exitReport)) { throw 'Installed application self-test did not run' }
$uninstall = Start-Process -FilePath (Join-Path $testDir 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -WindowStyle Hidden
if (!$uninstall.WaitForExit(60000) -or $uninstall.ExitCode -ne 0) { throw 'Uninstall failed' }
if (Test-Path -LiteralPath $exe) { throw 'Uninstall left application files' }
Write-Host 'Installer install / application launch / uninstall smoke test passed.'
