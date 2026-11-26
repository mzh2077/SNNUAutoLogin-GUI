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
$installLog = "$testDir-install.log"
$proc = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', "/DIR=`"$testDir`"", "/LOG=`"$installLog`"") -PassThru -WindowStyle Hidden
if (!$proc.WaitForExit(120000)) { throw 'Installer timed out' }
if ($proc.ExitCode -ne 0) { throw "Installer failed ($($proc.ExitCode)): $installLog" }
$exe = Join-Path $testDir 'SNNUAutoLogin.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Installed executable missing' }
$installedVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($installedVersion -ne $ExpectedFileVersion) { throw "Installed version mismatch: $installedVersion != $ExpectedFileVersion" }
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
