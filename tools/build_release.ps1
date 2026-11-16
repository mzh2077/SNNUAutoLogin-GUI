param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$InstallerCompiler
)
$ErrorActionPreference = 'Stop'
if (!$InstallerCompiler) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InstallerCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$InstallerCompiler) { throw 'Inno Setup 6 compiler not found. Install Inno Setup 6 or pass -InstallerCompiler.' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$bundle = & (Join-Path $PSScriptRoot 'build_portable.ps1') -OutputDirectory $destination -StageOnly
& $InstallerCompiler "/DSrcDir=$($bundle.PublishDirectory)" "/O$destination" (Join-Path $PSScriptRoot '..\installer\SNNUAutoLogin.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $destination "SNNUAutoLogin-Setup-$($bundle.Version).exe"
if (!(Test-Path -LiteralPath $setup)) { throw 'Installer output missing' }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($setup))`n")
Write-Host "Release ready: $destination"
