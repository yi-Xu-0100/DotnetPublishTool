<#
.SYNOPSIS
  按规范提交（Conventional Commits）生成 GitHub Release 正文。
.DESCRIPTION
  本仓库的提交一律是「<type>(<scope>): 中文主题」+ 空行 + 「- 说明」列表。本脚本取 <Tag> 之前最近一个 tag
  到 <Tag> 之间的提交（不含合并提交、不含 chore(release) 版本号提交），按类型分组渲染成 Markdown，
  供 CI 用 gh release create --notes-file 使用；本地也可单跑，看某次发版的正文长什么样。
.PARAMETER Tag
  目标 tag 或 ref（如 v1.1.2），默认 HEAD。该 ref 不存在时（手动触发、tag 还没建的场景）回退到 HEAD。
.PARAMETER From
  起始 tag（不含）。默认自动取 <Tag> 之前最近的 tag；一个都没有时从第一个提交开始。
.PARAMETER Output
  输出文件，默认 release-notes.md（已 gitignore）。
.EXAMPLE
  pwsh tools/make-release-notes.ps1 -Tag v1.1.2
#>
param(
    [string]$Tag = 'HEAD',
    [string]$From,
    [string]$Output = 'release-notes.md'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$tick = [char]96
$lf = [string][char]10

# ---------- 目标 ref（tag 不存在时回退 HEAD）----------
$target = $Tag
& git rev-parse --verify --quiet "$target^{commit}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  提示：ref '$Tag' 不存在，正文改按当前 HEAD 生成。"
    $target = 'HEAD'
}

# ---------- 起始 tag ----------
if ([string]::IsNullOrWhiteSpace($From)) {
    $From = (& git describe --tags --abbrev=0 "$target^" 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0) { $From = $null }
    if ([string]::IsNullOrWhiteSpace($From)) { $From = $null }
}

$range = if ($From) { "$From..$target" } else { $target }
Write-Host "  提交范围：$range"

# ---------- 取提交（%x01 分隔字段、%x02 分隔提交，正文多行也安全）----------
$format = '%H%x01%s%x01%b%x02'
$raw = (& git log --no-merges "--pretty=format:$format" $range) -join $lf
$commits = @($raw -split [char]2) | Where-Object { $_.Trim().Length -gt 0 }

$order = @('feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'style', 'chore')
$titles = @{
    feat = '✨ 新功能'; fix = '🐞 缺陷修复'; perf = '⚡ 性能'; refactor = '♻️ 重构'
    docs = '📝 文档'; test = '✅ 测试'; build = '📦 构建'; ci = '🔧 CI'; style = '🎨 样式'; chore = '🧹 其他'
}
$items = @{}
foreach ($key in $order) { $items[$key] = [System.Collections.Generic.List[string]]::new() }

$pattern = '^(?<type>[a-z]+)(\((?<scope>[^)]*)\))?!?:\s*(?<subject>.+)$'
$total = 0
$skipped = 0
foreach ($commit in $commits) {
    $parts = $commit -split [char]1
    if ($parts.Count -lt 2) { continue }
    $hash = $parts[0].Trim()
    $subject = $parts[1].Trim()
    $body = if ($parts.Count -ge 3) { $parts[2] } else { '' }

    $match = [regex]::Match($subject, $pattern)
    if (-not $match.Success) { $skipped++; continue }
    $type = $match.Groups['type'].Value
    $scope = $match.Groups['scope'].Value
    $text = $match.Groups['subject'].Value
    if ($type -eq 'chore' -and $scope -eq 'release') { $skipped++; continue }
    if (-not $items.ContainsKey($type)) { $type = 'chore' }

    $prefix = if ($scope) { '**' + $scope + '**：' } else { '' }
    $items[$type].Add('- ' + $prefix + $text + '（' + $tick + $hash.Substring(0, 7) + $tick + '）')

    # 提交正文里的「- 说明」作为子条目，保留上下文
    foreach ($bullet in ($body -split $lf)) {
        $trimmed = $bullet.Trim()
        if ($trimmed.StartsWith('- ')) { $items[$type].Add('  ' + $trimmed) }
    }
    $total++
}

# ---------- 组装 Markdown ----------
$versionTag = if ($target -eq 'HEAD') { ((& git describe --tags --abbrev=0 2>$null | Select-Object -First 1)) } else { $target }
if ([string]::IsNullOrWhiteSpace($versionTag)) { $versionTag = 'v0.0.0' }
$version = $versionTag -replace '^v', ''

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# DotnetPublishTool ' + $version)
$lines.Add('')
if ($total -eq 0) {
    $lines.Add('本次没有可归入正文的提交（只有版本号之类的提交）。')
}
else {
    $lines.Add('## 本次更新')
    foreach ($key in $order) {
        if ($items[$key].Count -eq 0) { continue }
        $lines.Add('')
        $lines.Add('### ' + $titles[$key])
        foreach ($item in $items[$key]) { $lines.Add($item) }
    }
}

$lines.Add('')
$lines.Add('## 下载')
$lines.Add('')
$lines.Add('- ' + $tick + 'DotnetPublishTool_' + $version + '_win-x64_framework-dependent.msi' + $tick + ' / ' + $tick + '.zip' + $tick + ' —— 体积小；目标机需 .NET 10 桌面运行时')
$lines.Add('- ' + $tick + 'DotnetPublishTool_' + $version + '_win-x64_self-contained.msi' + $tick + ' / ' + $tick + '.zip' + $tick + ' —— 自带运行时；体积大')

$remote = (& git remote get-url origin 2>$null | Select-Object -First 1)
if (-not [string]::IsNullOrWhiteSpace($remote)) {
    $url = $remote.Trim() -replace '^git@([^:]+):', 'https://$1/' -replace '\.git$', ''
    if ($From) { $lines.Add(''); $lines.Add('**完整变更**：' + $url + '/compare/' + $From + '...' + $versionTag) }
}

$text = [string]::Join($lf, $lines) + $lf
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $Output), $text, (New-Object System.Text.UTF8Encoding $false))
Write-Host "  已生成 $Output：$total 条提交进正文，跳过 $skipped 条（合并/版本号/不规范）"
