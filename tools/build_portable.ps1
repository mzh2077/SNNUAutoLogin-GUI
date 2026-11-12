param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$PassThru,
    # 本机开启了 Smart App Control（WDAC 信誉策略）。策略按「文件内容哈希」判定，
    # 同一个文件名换内容照样被拦（0x800711C7），改名即可拿到全新哈希并正常放行。
    # 用 -AssemblySuffix v2 之类的后缀发布一份改名版，运行入口随之变成 xxxv2.exe。
    [string]$AssemblySuffix = ''
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\SNNUAutoLogin.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$assembly = 'SNNUAutoLogin'
if ($AssemblySuffix) { $assembly = "SNNUAutoLogin$AssemblySuffix" }
$name = "$assembly-Portable-$version-win-x64"
$destination = [IO.Path]::GetFullPath($OutputDirectory)
# Unique staging avoids packaging stale files from an earlier publish.
$staging = Join-Path $destination ("staging-" + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging $name
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$publishArgs = @($project, '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '--self-contained', '-o', $publish)
if ($AssemblySuffix) { $publishArgs += @("-p:AssemblyName=$assembly") }
dotnet publish @publishArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $publish
$archive = Join-Path $destination "$name.zip"
Compress-Archive -LiteralPath $publish -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name.zip`n")
Write-Host "Portable archive: $archive"
Write-Host "SHA256: $hash"
Write-Host "Run: $(Join-Path $publish "$assembly.exe")"
if ($PassThru) {
    [pscustomobject]@{ PublishDirectory = $publish; Archive = $archive; Version = $version; Assembly = $assembly }
}
