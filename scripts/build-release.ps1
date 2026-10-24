[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw -Encoding utf8
    $Version = [string]$props.Project.PropertyGroup.Version
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw '未定义发布版本。' }
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'DeltaCrafter-Fixed-win-x64'
$zipName = "DeltaCrafter-Fixed-win-x64-$Version.zip"
$zipPath = Join-Path $artifacts $zipName
if (Test-Path -LiteralPath $artifacts) { throw 'artifacts 已存在，请先人工归档或使用干净工作副本。' }
& (Join-Path $PSScriptRoot 'Test-ReleasePrivacy.ps1') -Mode Source
New-Item -ItemType Directory -Path $artifacts | Out-Null
Push-Location $root
try {
    dotnet restore DeltaCrafter.sln --configfile nuget.config -p:RestorePackagesWithLockFile=true -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw '依赖还原失败。' }
    dotnet build DeltaCrafter.sln -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败。' }
    dotnet test tests/DeltaCrafter.Core.Tests/DeltaCrafter.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw '回归测试失败。' }
    dotnet publish src/DeltaCrafter.App/DeltaCrafter.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version --no-restore -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw '自包含发布失败。' }
    Copy-Item -LiteralPath README.md, LICENSE, THIRD-PARTY-NOTICES.md -Destination $publishDir
    Copy-Item -LiteralPath licenses -Destination $publishDir -Recurse
    Copy-Item -LiteralPath docs -Destination $publishDir -Recurse
    & (Join-Path $PSScriptRoot 'Test-ReleasePrivacy.ps1') -Mode Package -Path $publishDir
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $zipName" -Encoding ascii
}
finally { Pop-Location }
Write-Output "发布包：$zipPath"
Write-Output "校验文件：$zipPath.sha256"
