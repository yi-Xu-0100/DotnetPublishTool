using System.Globalization;
using System.Text.Json;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>本次自动递增的版本计划：基准版本与递增后的版本。</summary>
public readonly record struct MsiVersionPlan(string BaseVersion, string NextVersion);

/// <summary>
/// MSI 打包时的版本自动递增。
///   · 写回项目时，递增基准就是项目当前的版本（项目文件是唯一真相，手动改小也立刻生效）；
///   · 不写回时才跟本机记录取较大者，避免反复产出同号安装包；
///   · 发布成功后按选项写回项目 csproj（写前备份原文件），或只记进本机版本记录。
/// </summary>
public sealed class MsiVersionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxBackups = 5;

    private readonly object _gate = new();
    private Dictionary<string, string>? _state;
    private string? _statePath;

    public MsiVersionService(string? statePath = null) => _statePath = statePath;

    /// <summary>本机版本记录文件的实际路径（%APPDATA% 不可写时退到 %TEMP%）。</summary>
    public string StatePath => _statePath ??= ResolveWritableStatePath();

    /// <summary>算出本次要用的新版本；未启用自动递增时返回 null（调用方沿用原版本）。</summary>
    /// <summary>本机记录里这个项目上一次用的版本号（没记录过返回 null）。打包时用它提醒「版本号没变」。</summary>
    public string? LastRecordedVersion(string projectPath) => ReadState(projectPath);

    public MsiVersionPlan? Resolve(MsiOptions msi, string projectPath, string? effectiveVersion)
    {
        if (!msi.AutoBumpVersion)
        {
            return null;
        }

        // 写回模式下项目文件就是唯一真相：手动把版本改小/改回去要立刻生效。
        // 只有不写回时才需要跟本机记录取较大者 —— 否则每次都从同一个项目版本 +1，会反复产出同号安装包。
        var baseVersion = msi.WriteVersionBack
            ? effectiveVersion
            : Newer(effectiveVersion, ReadState(projectPath));
        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            throw new PublishException(
                "启用了「打包时自动递增版本」，但解析不出项目版本。" + Environment.NewLine
                + "请在项目里写 <Version>，或在「版本覆盖」里填一个版本号后重试。");
        }

        return new MsiVersionPlan(baseVersion!, Next(baseVersion!));
    }

    /// <summary>
    /// 把版本递增一位，保留原来的段数（1.4 → 1.5、1.4.2 → 1.4.3、2.1.2.0 → 2.1.3.0），尾段清零。
    /// 递增一定落在 MSI 看得到的前三段里 —— 否则安装包版本不变，升级会失效。
    /// 超出上限（前两段 ≤255、第三段 ≤65534，取 AssemblyVersion 与 MSI 里更严的那个）时自动进位。
    /// </summary>
    public static string Next(string version)
    {
        var fields = Parse(version);
        if (fields.Count == 0)
        {
            throw new PublishException("解析不出版本号：" + version + "（自动递增需要形如 1.2.3 的版本）");
        }

        var index = fields.Count >= 3 ? 2 : fields.Count - 1;
        fields[index]++;
        for (var i = index + 1; i < fields.Count; i++)
        {
            fields[i] = 0;
        }

        if (fields.Count >= 3 && fields[2] > 65534)
        {
            fields[2] = 0;
            fields[1]++;
        }

        if (fields.Count >= 2 && fields[1] > 255)
        {
            fields[1] = 0;
            fields[0]++;
        }

        if (fields[0] > 255)
        {
            throw new PublishException("版本号已经到 255 的上限，没法再自动递增：" + version + "（请手工改大段号）");
        }

        return string.Join('.', fields);
    }

    /// <summary>发布成功后调用：按选项写回项目 csproj 或记录版本；返回一句可写进日志的说明。</summary>
    public string Apply(
        string projectPath,
        string version,
        string? baseVersion,
        bool writeBack,
        string backupDirectory,
        IProgress<string>? log)
    {
        string description;
        if (writeBack)
        {
            var backup = Backup(projectPath, backupDirectory);
            CsprojVersionEditor.Write(projectPath, version, baseVersion);
            description = "已把项目版本更新为 " + version + "（原文件备份：" + backup + "）";
        }
        else
        {
            description = "已把 " + version + " 记为本机下一次的版本（源项目未改动）";
        }

        WriteState(projectPath, version);
        log?.Report("[版本] " + description);
        return description;
    }

    /// <summary>复制一份项目文件到输出目录当备份，并只保留最近几次。</summary>
    private static string Backup(string projectPath, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var target = Path.Combine(backupDirectory, name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csproj");
        File.Copy(projectPath, target, overwrite: true);

        var old = Directory.EnumerateFiles(backupDirectory, "*.csproj")
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .Skip(MaxBackups)
            .ToList();
        foreach (var path in old)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 清理旧备份失败不影响这次结果。
            }
        }

        return target;
    }

    /// <summary>把版本号拆成最多四段整数；带 -prerelease / +build 的部分直接丢掉。</summary>
    private static List<int> Parse(string? version)
    {
        var fields = new List<int>(4);
        var text = (version ?? string.Empty).Trim();
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        foreach (var part in text.Split('.'))
        {
            if (fields.Count == 4)
            {
                break;
            }

            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                break;
            }

            fields.Add(value);
        }

        return fields;
    }

    /// <summary>取两个版本里更大的那个（按段比较，缺的段补 0）。</summary>
    private static string? Newer(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a.Count == 0)
        {
            return b.Count == 0 ? null : right;
        }

        if (b.Count == 0)
        {
            return left;
        }

        return Compare(a, b) >= 0 ? left : right;
    }

    private static int Compare(List<int> a, List<int> b)
    {
        for (var i = 0; i < 4; i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }

        return 0;
    }

    private string? ReadState(string projectPath)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _state!.TryGetValue(projectPath, out var version) ? version : null;
        }
    }

    private void WriteState(string projectPath, string version)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _state![projectPath] = version;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_state is not null)
        {
            return;
        }

        _state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in StateCandidates())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var records = JsonSerializer.Deserialize<List<VersionRecord>>(File.ReadAllText(path), JsonOptions) ?? [];
                foreach (var record in records)
                {
                    if (!string.IsNullOrWhiteSpace(record.ProjectPath))
                    {
                        _state[record.ProjectPath] = record.Version ?? string.Empty;
                    }
                }

                _statePath = path;
                return;
            }
            catch
            {
                // 换下一个候选。
            }
        }
    }

    private void Save()
    {
        var payload = _state!
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new VersionRecord(pair.Key, pair.Value))
            .ToList();
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        foreach (var path in StateCandidates())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, json);
                _statePath = path;
                return;
            }
            catch
            {
                // 换下一个候选。
            }
        }
    }

    private IEnumerable<string> StateCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_statePath))
        {
            yield return _statePath!;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DotnetPublishTool",
            "msi-versions.json");
        yield return appData;

        var temp = Path.Combine(Path.GetTempPath(), "DotnetPublishTool", "msi-versions.json");
        if (!string.Equals(temp, appData, StringComparison.OrdinalIgnoreCase))
        {
            yield return temp;
        }
    }

    private string ResolveWritableStatePath()
    {
        foreach (var path in StateCandidates())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return path;
            }
            catch
            {
                // 换下一个候选。
            }
        }

        return Path.Combine(Path.GetTempPath(), "DotnetPublishTool", "msi-versions.json");
    }

    private sealed record VersionRecord(string ProjectPath, string? Version);
}
