param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-ui')
)
$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $Executable).Path
foreach ($theme in @('light', 'dark')) {
    $resultDir = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $theme))
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($appPath)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.ArgumentList.Add('--ui-test')
    if ($theme -eq 'dark') { $start.ArgumentList.Add('--dark') }
    $start.Environment['TEMP'] = $resultDir
    $start.Environment['TMP'] = $resultDir
    Write-Host "Checking $theme window, portal link, dialog styles and edge docking"
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(45000)) {
        $process.Kill()
        $errorFile = Join-Path $resultDir 'snnu_hook_error.txt'
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Host }
        throw "$theme UI check did not finish"
    }
    $report = Join-Path $resultDir 'snnu_uitest.txt'
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "$theme UI check failed" }
    if (!(Select-String -LiteralPath $report -SimpleMatch 'PASS portal hyperlink and shared dialog input style')) {
        throw "$theme portal and style check missing"
    }
    foreach ($check in @('PASS explicit taskbar icons and outlet switch menu',
        'PASS concurrent dialogs, dialog teardown and late captcha decoding',
        'PASS display changes, reduced work area and rebuilt view layout',
        'PASS native close defers view teardown after display changes')) {
        if (!(Select-String -LiteralPath $report -SimpleMatch $check)) { throw "$theme missing regression check: $check" }
    }
    Get-Content -LiteralPath $report | Write-Host
}
