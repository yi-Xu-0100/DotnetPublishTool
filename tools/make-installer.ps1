#requires -Version 5.1
<#
.SYNOPSIS
  把 DotnetPublishTool 自己打包成一个 MSI 安装包（安装到 Program Files）。

.DESCRIPTION
  两步：
    1) dotnet publish 发布 WPF 界面（默认框架依赖 + win-x64）；
    2) 用仓库自带的打包能力把发布目录做成 MSI
       （dotnet run --project tools/DotnetPublishTool.Smoke -- pack-msi ...）。

  生成的 MSI 双击即可安装到 %ProgramFiles%\DotnetPublishTool，并出现在「程序和功能」里。静默安装/卸载：

    msiexec /i DotnetPublishTool_1.0.0.msi /qn
    msiexec /x DotnetPublishTool_1.0.0.msi /qn

  升级：升级代码由「发布者 + 产品名」稳定推导，只要两者不变，安装新版本时会先卸掉旧版本。

  注意：框架依赖发布要求目标机器已装 .NET 10 桌面运行时；要让安装包含运行时用 -SelfContained
  （体积大，且需要能访问 NuGet 还原运行时包）。

  必须在【沙箱之外】的普通终端运行：沙箱里 dotnet publish 写不了项目的 obj（Low 完整性会继承到产物）。

.PARAMETER Configuration
  生成配置，默认 Release。

.PARAMETER RuntimeIdentifier
  运行时标识符，默认 win-x64。

.PARAMETER OutputDirectory
  安装包输出目录，默认 <仓库>\artifacts\installer。

.PARAMETER ProductName
  「程序和功能」与开始菜单里显示的产品名，同时决定安装目录名；默认 DotnetPublishTool。

.PARAMETER Manufacturer
  发布者，默认 DotnetPublishTool。

.PARAMETER Version
  安装版本（会规整成 x.y.z），默认取 App csproj 里的 <Version>（版本号唯一来源，与 CI 一致）。

.PARAMETER UpgradeCode
  固定升级代码；默认按「发布者 + 产品名」推导。

.PARAMETER SelfContained
  自包含发布（安装包自带 .NET 运行时）。

.PARAMETER NoShortcut
  不创建开始菜单快捷方式。

.PARAMETER SkipPublish
  跳过发布，直接用已有的发布产物（配合 -PublishDirectory）打包。

.PARAMETER PublishDirectory
  发布产物目录；默认 <OutputDirectory>\publish。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\make-installer.ps1
  powershell -ExecutionPolicy Bypass -File .\tools\make-installer.ps1 -Version 1.1.0 -Manufacturer "某某公司"
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory,
    [string]$ProductName = 'DotnetPublishTool',
    [string]$Manufacturer = 'DotnetPublishTool',
    [string]$Version,
    [string]$UpgradeCode,
    [string]$PublishDirectory,
    [switch]$SelfContained,
    [switch]$NoShortcut,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repo 'src\DotnetPublishTool.App\DotnetPublishTool.App.csproj'
$smokeProject = Join-Path $repo 'tools\DotnetPublishTool.Smoke\DotnetPublishTool.Smoke.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo 'artifacts\installer'
}
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $OutputDirectory 'publish'
}

# 沙箱内 dotnet publish 会因 obj 是 Medium 而失败，产物也会带 Low 标签：直接拦住。
if ([bool](((whoami /groups) 2>$null) | Select-String 'S-1-16-4096')) {
    throw '当前会话是低完整性（DSH 沙箱内）：dotnet publish 写不了项目目录下的 obj，产物也会继承 Low 标签。请在普通 PowerShell 或 Visual Studio 的终端里运行本脚本。'
}

Write-Host "仓库：$repo"
Write-Host "输出：$OutputDirectory"

if (-not $SkipPublish) {
    Write-Host '发布界面…'
    $publishArgs = @('publish', $projectPath, '-c', $Configuration, '-o', $PublishDirectory)
    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $publishArgs += @('-r', $RuntimeIdentifier)
    }
    $publishArgs += ('--self-contained:{0}' -f $SelfContained.IsPresent.ToString().ToLowerInvariant())

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）。" }
}

if (-not (Test-Path (Join-Path $PublishDirectory 'DotnetPublishTool.exe'))) {
    throw "发布目录里没有 DotnetPublishTool.exe：$PublishDirectory"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    # 注意：只请求一个属性时 MSBuild 打印裸值，请求两个属性才输出 JSON —— 两种形状都要认。
    $evaluated = & dotnet msbuild $projectPath -nologo -getProperty:Version -getProperty:Configuration
    $text = ($evaluated | Out-String).Trim()
    if ($text.StartsWith('{')) {
        try { $Version = ($text | ConvertFrom-Json).Properties.Version } catch { $Version = $null }
    }
    else { $Version = $text }

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw '取不到 App 项目的 <Version>：请在 DotnetPublishTool.App.csproj 里写 <Version>，或用 -Version 指定。'
    }

    Write-Host "版本号（来自 csproj）：$Version"
}

Write-Host "打包 MSI（产品名 $ProductName，版本 $Version）…"
$msiArgs = @(
    'run', '--project', $smokeProject, '--', 'pack-msi', $PublishDirectory,
    '--out', $OutputDirectory,
    '--msi-product', $ProductName,
    '--msi-manufacturer', $Manufacturer,
    '--msi-version', $Version
)
if (-not [string]::IsNullOrWhiteSpace($UpgradeCode)) { $msiArgs += @('--msi-upgrade-code', $UpgradeCode) }
if ($NoShortcut) { $msiArgs += '--no-msi-shortcut' }

& dotnet @msiArgs
if ($LASTEXITCODE -ne 0) { throw "打包 MSI 失败（退出码 $LASTEXITCODE）。" }

$msi = Get-ChildItem $OutputDirectory -Filter '*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ''
Write-Host "安装包：$($msi.FullName)" -ForegroundColor Green
Write-Host ('静默安装：msiexec /i "' + $msi.FullName + '" /qn')
Write-Host ('静默卸载：msiexec /x "' + $msi.FullName + '" /qn')
