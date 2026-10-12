#Requires -Version 5.1
<#
.SYNOPSIS
  在「管理员终端」里验证 MSI 的升级行为：装旧版 → 装新版 → 断言旧版被卸掉、文件被换成新版。

.DESCRIPTION
  用当前源码现场产出两个版本（默认 9.8.0 / 9.9.0）的安装包，产品名默认是临时名
  DotnetUpgradeProbe —— 不碰你真正装着的 DotnetPublishTool，装到临时目录、跑完自动卸载并清理。
  两版载荷里各放一个标记文件（old-only.txt / new-only.txt），据此判断：
    · 新版文件到位（new-only.txt 在）；
    · 旧版文件被清掉（old-only.txt 不在）—— 这条证明旧产品真的被卸掉，而不只是注册表里剩一条；
    · 程序和功能里只剩新版一条记录。

  这是唯一能可信验证「每机器安装 + 升级卸载」的方式：沙箱/非提权会话里做不了每机器安装
  （会报 Error 1925），所以这个脚本必须在「以管理员身份运行」的终端里跑。

.PARAMETER OldMsi / NewMsi
  直接用现成的两个安装包（跳过现场打包）。两个都要给。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\verify-msi-upgrade.ps1

.EXAMPLE
  # 直接验证两个已发布/已下载的安装包
  powershell -ExecutionPolicy Bypass -File .\tools\verify-msi-upgrade.ps1 -OldMsi "$env:USERPROFILE\Downloads\DotnetPublishTool_1.0.0_win-x64_framework-dependent.msi" -NewMsi "$env:USERPROFILE\Downloads\DotnetPublishTool_1.1.0_win-x64_framework-dependent.msi"
#>
[CmdletBinding()]
param(
    [string]$ProductName = 'DotnetUpgradeProbe',
    [string]$Manufacturer = 'DotnetUpgradeProbe',
    [string]$OldVersion = '9.8.0',
    [string]$NewVersion = '9.9.0',
    [string]$WorkDirectory = (Join-Path $env:TEMP 'DotnetPublishTool-msi-upgrade-check'),
    [string]$OldMsi,
    [string]$NewMsi,
    # 矩阵模式：逐个改新版包的升级声明（Remove / REP 位置 / Attributes），看哪一种能让旧版真被卸掉。
    [switch]$ProbeVariants
)

$ErrorActionPreference = 'Stop'
# 让 dotnet 等子进程的 UTF-8 输出不要在控制台里变成乱码。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$script:Failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) { Write-Host ''; Write-Host ('== ' + $text) -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host ('  [OK] ' + $text) -ForegroundColor Green }
function Write-Bad([string]$text) { Write-Host ('  [!!] ' + $text) -ForegroundColor Red; $script:Failures.Add($text) }
function Write-Note([string]$text) { Write-Host ('      ' + $text) -ForegroundColor DarkGray }

function Get-ProbeProducts {
    $items = @()
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        foreach ($key in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -eq $ProductName) {
                $items += [pscustomobject]@{
                    ProductCode = $key.PSChildName
                    Version     = $props.DisplayVersion
                    Location    = $props.InstallLocation
                }
            }
        }
    }
    return $items
}

function Invoke-Msi([string]$arguments, [string]$what) {
    Write-Note ("msiexec " + $arguments)
    $process = Start-Process 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru
    Write-Note ($what + ' 退出码 = ' + $process.ExitCode)
    return $process.ExitCode
}

function Show-LogHighlights([string]$log, [string]$title) {
    if (-not (Test-Path $log)) { return }
    Write-Note ('--- ' + $title + ' ---')
    Select-String -Path $log -Pattern 'FindRelatedProducts|UPGRADEFOUND =|RemoveExistingProducts|Error \d+\.|Installation success|Installation failed|Note: 1: 1724' |
        Select-Object -First 12 | ForEach-Object { Write-Note ('  ' + $_.Line.Trim()) }
}

# ---------- 0) 环境检查：必须管理员，且不能在沙箱里 ----------
Write-Step '环境检查'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '不是管理员会话。请用「以管理员身份运行」打开 PowerShell 后再执行本脚本。'
}
if ([bool](((whoami /groups) 2>$null) | Select-String 'S-1-16-4096')) {
    throw '当前是低完整性会话（DSH 沙箱）：沙箱里做不了每机器安装/卸载，请在普通的管理员终端里运行。'
}
Write-Ok '管理员会话'

$repo = Split-Path -Parent $PSScriptRoot
$smokeProject = Join-Path $repo 'tools\DotnetPublishTool.Smoke\DotnetPublishTool.Smoke.csproj'
$appProject = Join-Path $repo 'src\DotnetPublishTool.App\DotnetPublishTool.App.csproj'

if (Test-Path $WorkDirectory) { Remove-Item $WorkDirectory -Recurse -Force }
$logsDirectory = Join-Path $WorkDirectory 'logs'
$msiDirectory = Join-Path $WorkDirectory 'msi'
$payloadOld = Join-Path $WorkDirectory 'payload-old'
$payloadNew = Join-Path $WorkDirectory 'payload-new'
foreach ($directory in $logsDirectory, $msiDirectory, $payloadOld, $payloadNew) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$installDirectory = Join-Path $WorkDirectory 'app'

try {
    # ---------- 1) 两个版本的安装包 ----------
    if ([string]::IsNullOrWhiteSpace($OldMsi) -or [string]::IsNullOrWhiteSpace($NewMsi)) {
        Write-Step '现场产出两个版本的安装包（发布 App → 各自加标记文件 → pack-msi）'
        & dotnet publish $appProject -c Release -o $payloadOld | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）。" }
        if (-not (Test-Path (Join-Path $payloadOld 'DotnetPublishTool.exe'))) { throw "发布目录里没有 DotnetPublishTool.exe：$payloadOld" }

        Copy-Item (Join-Path $payloadOld '*') $payloadNew -Recurse -Force
        '这个文件只属于旧版本，升级成功的话它应该被删掉。' | Set-Content (Join-Path $payloadOld 'old-only.txt') -Encoding UTF8
        '这个文件只属于新版本，升级成功的话它应该出现。' | Set-Content (Join-Path $payloadNew 'new-only.txt') -Encoding UTF8

        foreach ($pair in @(@($OldVersion, $payloadOld), @($NewVersion, $payloadNew))) {
            $version = $pair[0]
            $payload = $pair[1]
            & dotnet run --project $smokeProject -- pack-msi $payload --out $msiDirectory --msi-product $ProductName --msi-manufacturer $Manufacturer --msi-version $version --no-msi-shortcut | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "pack-msi 失败（版本 $version，退出码 $LASTEXITCODE）。" }
        }

        $OldMsi = (Get-ChildItem $msiDirectory -Filter ('*' + $OldVersion + '*.msi') | Select-Object -First 1).FullName
        $NewMsi = (Get-ChildItem $msiDirectory -Filter ('*' + $NewVersion + '*.msi') | Select-Object -First 1).FullName
    }
    else {
        Write-Step '用现成的两个安装包'
        $OldMsi = (Resolve-Path $OldMsi).Path
        $NewMsi = (Resolve-Path $NewMsi).Path
    }
    Write-Ok ('旧版包：' + $OldMsi)
    Write-Ok ('新版包：' + $NewMsi)

    if ($ProbeVariants) {
        Write-Step '变体矩阵：逐个改新版包的升级声明，看哪一种能让旧版被卸掉'

        # 位定义：1=MigrateFeatures、2=OnlyDetect（只检测、不卸载）、4=IgnoreRemoveFailure、
        #         256=VersionMinInclusive、512=VersionMaxInclusive、1024=LanguagesExclusive。
        # 变体靠工具自身的诊断环境变量重打（不碰 COM 改包 —— 那样会锁住 msi，msiexec 直接报 1619）。
        if (-not (Test-Path (Join-Path $payloadNew 'DotnetPublishTool.exe'))) {
            throw '矩阵模式需要现场打包（payload-new 不在）：不要和 -OldMsi / -NewMsi 一起用。'
        }
        $variants = @(
            @{ Name = '基线：256 VersionMinInclusive（工具现在的写法）'; Attributes = 256; Expect = 'ok' },
            @{ Name = '旧写法：3（MigrateFeatures|OnlyDetect）'; Attributes = 3; Expect = 'still' },
            @{ Name = '258（VersionMinInclusive|OnlyDetect）'; Attributes = 258; Expect = 'still' },
            @{ Name = '512（VersionMaxInclusive）'; Attributes = 512; Expect = 'ok' },
            @{ Name = '257（MigrateFeatures|VersionMinInclusive）'; Attributes = 257; Expect = 'ok' },
            @{ Name = '259（MigrateFeatures|OnlyDetect|VersionMinInclusive）'; Attributes = 259; Expect = 'still' }
        )
        $results = New-Object System.Collections.Generic.List[string]
        $variantIndex = 0
        foreach ($variant in $variants) {
            $variantIndex++
            Write-Step ('变体 ' + $variantIndex + '：' + $variant.Name)
            foreach ($leftover in @(Get-ProbeProducts)) { [void](Invoke-Msi ('/x "' + $leftover.ProductCode + '" /qn /norestart') '清理残留') }
            $variantMsi = Join-Path $msiDirectory ('variant-' + $variantIndex + '.msi')
            $env:DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES = [string]$variant.Attributes
            try {
                & dotnet run --project $smokeProject -- pack-msi $payloadNew --out $msiDirectory --msi-product $ProductName --msi-manufacturer $Manufacturer --msi-version $NewVersion --no-msi-shortcut | Out-Null
                if ($LASTEXITCODE -ne 0) { throw ('pack-msi 失败（退出码 ' + $LASTEXITCODE + '）') }
            }
            finally {
                Remove-Item Env:DOTNETPUBLISHTOOL_UPGRADE_ATTRIBUTES -ErrorAction SilentlyContinue
            }
            $packed = (Get-ChildItem $msiDirectory -Filter ('*' + $NewVersion + '*.msi') | Select-Object -First 1).FullName
            Copy-Item $packed $variantMsi -Force
            Write-Note ('已重打成变体包：Attributes=' + $variant.Attributes)
            $oldLog = Join-Path $logsDirectory ('variant-' + $variantIndex + '-old.log')
            $newLog = Join-Path $logsDirectory ('variant-' + $variantIndex + '-new.log')
            $oldCode = Invoke-Msi ('/i "' + $OldMsi + '" /qn /norestart /l*v "' + $oldLog + '" INSTALLFOLDER="' + $installDirectory + '"') '装旧版'
            $newCode = Invoke-Msi ('/i "' + $variantMsi + '" /qn /norestart /l*v "' + $newLog + '" INSTALLFOLDER="' + $installDirectory + '"') '装新版'
            $remaining = @(Get-ProbeProducts)
            $oldMarkerGone = -not (Test-Path (Join-Path $installDirectory 'old-only.txt'))
            $suffix = '还在'
            if ($oldMarkerGone) { $suffix = '已删' }
            $actual = 'still'
            if ($remaining.Count -eq 1 -and $oldMarkerGone) { $actual = 'ok' }
            $state = '旧版仍在'
            if ($actual -eq 'ok') { $state = '旧版被卸掉' }
            $match = '符合预期'
            if ($actual -ne $variant.Expect) { $match = '不符合预期' }
            $verdict = $match + '：' + $state + '（记录 ' + $remaining.Count + ' 条，old-only.txt ' + $suffix + '，装旧/新版退出码 ' + $oldCode + '/' + $newCode + '）'
            $results.Add('  ' + $variant.Name + ' -> ' + $verdict)
            Write-Host ('      结果：' + $verdict)
        }
        Write-Host ''
        Write-Host '===== 变体矩阵结论 =====' -ForegroundColor Cyan
        $results | ForEach-Object { Write-Host $_ }
        Write-Host ('日志留在：' + $logsDirectory)
        Write-Host '把上面这几行（和 logs 目录）发回即可。'
        return
    }

    # ---------- 2) 装旧版 ----------
    Write-Step ('安装旧版 ' + $OldVersion + '（每机器，装到 ' + $installDirectory + '）')
    $oldLog = Join-Path $logsDirectory 'install-old.log'
    $code = Invoke-Msi ('/i "' + $OldMsi + '" /qn /norestart /l*v "' + $oldLog + '" INSTALLFOLDER="' + $installDirectory + '"') '安装旧版'
    if ($code -ne 0) { throw ('安装旧版失败（退出码 ' + $code + '），看日志：' + $oldLog) }
    $oldProducts = @(Get-ProbeProducts)
    if ($oldProducts.Count -ne 1) { Write-Bad ('装完旧版后，程序和功能里应该有 1 条记录，实际 ' + $oldProducts.Count + ' 条') } else { Write-Ok ('旧版注册记录：' + $oldProducts[0].ProductCode + ' v' + $oldProducts[0].Version) }
    if (Test-Path (Join-Path $installDirectory 'old-only.txt')) { Write-Ok '旧版标记文件已就位' } else { Write-Bad '旧版标记文件没有落盘' }

    # ---------- 3) 升级到新版 ----------
    Write-Step ('升级安装新版 ' + $NewVersion)
    $newLog = Join-Path $logsDirectory 'install-new.log'
    $code = Invoke-Msi ('/i "' + $NewMsi + '" /qn /norestart /l*v "' + $newLog + '" INSTALLFOLDER="' + $installDirectory + '"') '安装新版'
    if ($code -ne 0) { throw ('安装新版失败（退出码 ' + $code + '），看日志：' + $newLog) }

    # ---------- 4) 断言 ----------
    Write-Step '检查升级结果'
    $products = @(Get-ProbeProducts)
    if ($products.Count -eq 1 -and $products[0].Version -eq $NewVersion) {
        Write-Ok ('程序和功能里只剩新版一条：' + $products[0].ProductCode + ' v' + $products[0].Version)
    }
    else {
        Write-Bad ('升级后应只剩 1 条新版记录，实际 ' + $products.Count + ' 条：' + (($products | ForEach-Object { $_.Version }) -join ' + '))
        Write-Bad '旧产品没有被卸载（RemoveExistingProducts 没生效）—— 这就是要查的问题，请把下面的日志发回来。'
    }

    $newMarker = Join-Path $installDirectory 'new-only.txt'
    $oldMarker = Join-Path $installDirectory 'old-only.txt'
    if (Test-Path $newMarker) { Write-Ok '新版文件已铺上（new-only.txt 在）' } else { Write-Bad '新版文件没铺上（new-only.txt 不在）' }
    if (Test-Path $oldMarker) { Write-Bad '旧版文件还在（old-only.txt 没被删）' } else { Write-Ok '旧版文件已被清掉（old-only.txt 不在）' }

    $exe = Join-Path $installDirectory 'DotnetPublishTool.exe'
    if (Test-Path $exe) { Write-Ok ('安装目录里的 exe 版本：' + (Get-Item $exe).VersionInfo.FileVersion) }

    Show-LogHighlights $newLog '新版安装日志里的升级动作'

    # ---------- 5) 卸载，确认能卸干净 ----------
    Write-Step '卸载新版'
    $uninstallLog = Join-Path $logsDirectory 'uninstall-new.log'
    $code = Invoke-Msi ('/x "' + $NewMsi + '" /qn /norestart /l*v "' + $uninstallLog + '"') '卸载新版'
    if ($code -eq 0) { Write-Ok '卸载成功' } else { Write-Bad ('卸载失败（退出码 ' + $code + '）') }
    if ((Get-ProbeProducts).Count -eq 0) { Write-Ok '程序和功能里已无残留记录' } else { Write-Bad '卸载后仍有残留记录' }
}
finally {
    Write-Step '清理'
    foreach ($product in (Get-ProbeProducts)) {
        Write-Note ('残留产品，尝试卸载：' + $product.ProductCode)
        [void](Invoke-Msi ('/x "' + $product.ProductCode + '" /qn /norestart') '清理残留')
    }
    if (Test-Path $installDirectory) { Remove-Item $installDirectory -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Note ('日志留在：' + $logsDirectory)
}

Write-Host ''
if ($script:Failures.Count -eq 0) {
    Write-Host '结论：升级行为正常（旧版被卸载、文件被替换、只剩一条记录）。' -ForegroundColor Green
    exit 0
}

Write-Host ('结论：有 ' + $script:Failures.Count + ' 项不符合预期：') -ForegroundColor Red
$script:Failures | ForEach-Object { Write-Host ('  - ' + $_) -ForegroundColor Red }
Write-Host '请把上面这些行和 logs 目录里的日志一起发回，我好定位。' -ForegroundColor Yellow
exit 1
