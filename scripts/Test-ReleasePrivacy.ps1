[CmdletBinding()]
param(
    [ValidateSet('Source', 'Package')]
    [string]$Mode = 'Source',
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrEmpty($Path)) { $Path = $root }
$target = (Resolve-Path -LiteralPath $Path).Path
$sourceRoots = @('src', 'tests', 'docs', 'scripts', 'licenses')
$sourceFiles = @('.editorconfig', '.gitattributes', '.gitignore', 'CHANGELOG.md', 'CONTRIBUTING.md', 'DeltaCrafter.sln', 'Directory.Build.props', 'global.json', 'LICENSE', 'nuget.config', 'README.md', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md')
if ($Mode -eq 'Source') {
    if ($target -ne $root) { throw '源码扫描必须针对当前仓库。' }
    $files = @(
        foreach ($name in $sourceFiles) { Get-Item -LiteralPath (Join-Path $root $name) }
        foreach ($name in $sourceRoots) {
            Get-ChildItem -LiteralPath (Join-Path $root $name) -File -Recurse |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
    )
} else {
    $files = @(Get-ChildItem -LiteralPath $target -File -Recurse -Force)
}
$privateValues = @($env:USERNAME, $env:COMPUTERNAME, $env:USERPROFILE, $root) |
    Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
$deniedPatterns = @(
    'gh[pousr]_[A-Za-z0-9]{30,}',
    'github_pat_[A-Za-z0-9_]{40,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?im)(?:api[_-]?key|access[_-]?token|password)\s*[=:]\s*["''][A-Za-z0-9_+/=-]{20,}["'']'
)
$failures = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($target, $file.FullName)
    if ($file.Name -match '^(settings|plan|state)\.json(?:\..*)?$' -or
        $file.Extension -in @('.pdb', '.log', '.binlog', '.user', '.bak', '.dmp') -or
        $file.Name -match '^\.env(?:\..*)?$' -or
        $relative -match '(^|[\\/])(shots|logs|\.github|\.git)([\\/]|$)') {
        $failures.Add("$relative : 禁止的运行数据或调试文件")
    }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        foreach ($value in $privateValues) {
            if ($text.Contains($value, [StringComparison]::OrdinalIgnoreCase) -or
                $text.Contains($value.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -or
                $text.Contains($value.Replace('\', '\\'), [StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add("$relative : 当前构建环境标识")
                break
            }
        }
        foreach ($pattern in $deniedPatterns) {
            if ([regex]::IsMatch($text, $pattern)) { $failures.Add("$relative : 疑似凭据") }
        }
    }
}
if ($failures.Count -gt 0) {
    $failures | Sort-Object -Unique | Write-Output
    throw '隐私检查未通过；输出仅列相对文件名，不显示匹配内容。'
}
Write-Output "隐私检查通过：$Mode，共 $($files.Count) 个文件。还需人工复核未知信息和提交历史。"
