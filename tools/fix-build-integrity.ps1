#requires -Version 5.1
<#
.SYNOPSIS
  修复 DSH 沙箱遗留的「Low 完整性标签污染」，让 DotnetPublishTool 以 Medium 完整性运行。

.DESCRIPTION
  现象：应用能启动，但写「文档」或 %APPDATA% 时报
        System.UnauthorizedAccessException: Access to the path
        'C:\Users\<你>\Documents\DotnetPublishTool\<项目名>\profiles' is denied.

  根因：DSH 沙箱会给工作区（含 bin / obj）打上【可继承的】Low Mandatory Level，编译产物 exe/dll 继承该标签；
        而【带 Low 标签的可执行文件启动后以 Low 完整性运行】。Low 进程只能写 Low 对象，
        写 Documents / %APPDATA% 这类 Medium 对象一律被拒 —— 与 ACL 无关，也与「从哪里启动」无关
        （双击、Visual Studio、普通终端都一样）。

  本脚本把仓库内所有 bin / obj（以及 artifacts）的完整性标签升回 Medium。
  必须在【沙箱之外】用普通 PowerShell 运行：沙箱内 icacls 会返回 exit 5。

.PARAMETER Repo
  仓库根目录，默认取本脚本上一级目录（tools 的父目录）。

.PARAMETER Verify
  只检查不改动。

.NOTES
  运行后这些目录变为 Medium，低完整性的沙箱将无法再写入它们 —— 后续构建请在 Visual Studio 或普通终端进行。
#>
[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if (-not (Test-Path $Repo)) { throw "找不到仓库目录：$Repo" }

function Get-IntegrityLabel([string]$path) {
    $line = (icacls $path 2>$null | Select-String 'Mandatory Label' | Select-Object -First 1).Line
    if (-not $line) { return '无标签(=Medium)' }
    if ($line -match 'Low')    { return 'Low' }
    if ($line -match 'Medium') { return 'Medium' }
    if ($line -match 'High')   { return 'High' }
    if ($line -match 'System') { return 'System' }
    return $line.Trim()
}

$inSandbox = [bool](((whoami /groups) 2>$null) | Select-String 'S-1-16-4096')

Write-Host "仓库：$Repo"
Write-Host ("当前会话：{0}" -f $(if ($inSandbox) { '低完整性（沙箱内）' } else { 'Medium 或更高' }))

$targets = @(
    Get-ChildItem -Path $Repo -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { ($_.Name -eq 'bin' -or $_.Name -eq 'obj') -and $_.FullName -notmatch '\\\\\.git\\\\' } |
        Select-Object -ExpandProperty FullName
)
$artifacts = Join-Path $Repo 'artifacts'
if (Test-Path $artifacts) { $targets += $artifacts }
$targets = @($targets | Sort-Object -Unique)

if ($targets.Count -eq 0) {
    Write-Host '仓库内没有找到 bin / obj —— 先构建一次再运行本脚本。'
    return
}

Write-Host "目标目录：$($targets.Count) 个"

if ($Verify) {
    foreach ($t in $targets) {
        Write-Host ("  {0,-16} {1}" -f (Get-IntegrityLabel $t), $t.Replace($Repo + '\', ''))
    }
    return
}

if ($inSandbox) {
    Write-Host ''
    Write-Host '检测到当前是低完整性（沙箱内）会话：icacls 改不了完整性标签。' -ForegroundColor Yellow
    Write-Host '请在【普通 PowerShell 窗口】里重新运行：' -ForegroundColor Yellow
    Write-Host ('  powershell -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"') -ForegroundColor Yellow
    exit 1
}

$ok = 0
$fail = 0
foreach ($t in $targets) {
    # 注意：'(OI)(CI)Medium' 必须整体加引号，否则 PowerShell 会把它拆成多个表达式
    $null = icacls $t /setintegritylevel '(OI)(CI)Medium' /T 2>&1
    if ($LASTEXITCODE -eq 0) { $ok++ } else { $fail++; Write-Warning "处理失败：$t" }
}
Write-Host "完成：成功 $ok 个，失败 $fail 个"

$remaining = @($targets | Where-Object { (Get-IntegrityLabel $_) -eq 'Low' })
if ($remaining.Count -eq 0) {
    Write-Host 'OK：目标目录均非 Low，应用现在应以 Medium 完整性运行。' -ForegroundColor Green
    Write-Host '已构建的 exe 也已一并升级，直接用现有 exe 重新启动即可。'
} else {
    Write-Host "仍有 $($remaining.Count) 个目录为 Low：" -ForegroundColor Yellow
    $remaining | ForEach-Object { Write-Host "  $_" }
    exit 1
}
