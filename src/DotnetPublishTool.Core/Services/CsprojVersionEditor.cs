using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 读写项目文件里的版本键：@BT<Version>@BT、@BT<AssemblyVersion>@BT、@BT<FileVersion>@BT、@BT<InformationalVersion>@BT。
/// 只有「MSI 打包时自动递增并写回项目」这个显式选项会用到它；调用方必须先备份原文件。
/// 写的时候只替换这些元素的文本（不做 XML 往返），注释、缩进、空行、换行符与编码都不受影响。
/// </summary>
internal static partial class CsprojVersionEditor
{
    /// <summary>读第一个 @BT<Version>@BT（没有就依次看 AssemblyVersion / FileVersion / InformationalVersion）。</summary>
    public static string? Read(string projectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            foreach (var name in new[] { "Version", "AssemblyVersion", "FileVersion", "InformationalVersion" })
            {
                var value = document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把「值等于 baseVersion 的那些版本键」都改成新版本（例如项目同时写了
    /// AssemblyVersion / FileVersion / InformationalVersion）；一个都匹配不上时退回改或插 @BT<Version>@BT。
    /// </summary>
    public static void Write(string projectPath, string version, string? baseVersion)
    {
        var bytes = File.ReadAllBytes(projectPath);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            throw new PublishException("项目文件不是 UTF-8 编码，没法安全地写回版本：" + projectPath);
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = File.ReadAllText(projectPath);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // 1) 先收集「值等于基准版本」的版本键，只改它们。
        var targets = new List<(int Start, int Length)>();
        foreach (Match match in VersionElements().Matches(text))
        {
            var value = match.Groups["value"];
            if (baseVersion is null || NumericallyEqual(value.Value, baseVersion))
            {
                targets.Add((value.Index, value.Length));
            }
        }

        if (targets.Count > 0)
        {
            var buffer = new StringBuilder(text);
            for (var i = targets.Count - 1; i >= 0; i--)
            {
                buffer.Remove(targets[i].Start, targets[i].Length);
                buffer.Insert(targets[i].Start, version);
            }

            File.WriteAllText(projectPath, buffer.ToString(), new UTF8Encoding(hasBom));
            return;
        }

        // 2) 没有匹配的键（例如版本来自「版本覆盖」）→ 退回只处理 <Version>：改已有的 / 把自闭合补全 / 插进 PropertyGroup。
        var element = VersionElement().Match(text);
        var selfClosing = VersionSelfClosing().Match(text);
        string updated;

        if (element.Success)
        {
            var value = element.Groups["value"];
            updated = text[..value.Index] + version + text[(value.Index + value.Length)..];
        }
        else if (selfClosing.Success)
        {
            var name = selfClosing.Groups["name"].Value;
            updated = text[..selfClosing.Index]
                + "<" + name + ">" + version + "</" + name + ">"
                + text[(selfClosing.Index + selfClosing.Length)..];
        }
        else
        {
            var group = PropertyGroup().Match(text);
            if (!group.Success)
            {
                throw new PublishException(
                    "项目文件里既没有版本键也没有 <PropertyGroup>，没法写回版本：" + projectPath);
            }

            var insertAt = group.Index + group.Length;
            var indentMatch = Regex.Match(text[insertAt..], @"\r?\n(?<indent>[ \t]*)<");
            var indent = indentMatch.Success ? indentMatch.Groups["indent"].Value : "    ";
            updated = text.Insert(insertAt, newLine + indent + "<Version>" + version + "</Version>");
        }

        File.WriteAllText(projectPath, updated, new UTF8Encoding(hasBom));
    }

    /// <summary>四个版本键的配对形式（允许带命名空间前缀与其他属性）。</summary>
    [GeneratedRegex(@"<(?<name>(?:[A-Za-z_][\w.\-]*:)?(?:InformationalVersion|AssemblyVersion|FileVersion|Version))(?:\s[^>]*)?>(?<value>[^<]*)</\k<name>\s*>", RegexOptions.Singleline)]
    private static partial Regex VersionElementsPattern();

    /// <summary>@BT<Version>值</Version>@BT（退回路径用）。</summary>
    [GeneratedRegex(@"<(?:[A-Za-z_][\w.\-]*:)?Version(?:\s[^>]*)?>(?<value>[^<]*)</(?:[A-Za-z_][\w.\-]*:)?Version\s*>", RegexOptions.Singleline)]
    private static partial Regex VersionElementPattern();

    [GeneratedRegex(@"<(?<name>(?:[A-Za-z_][\w.\-]*:)?Version)(?:\s[^>]*)?/>", RegexOptions.Singleline)]
    private static partial Regex VersionSelfClosingPattern();

    [GeneratedRegex(@"<(?<name>(?:[A-Za-z_][\w.\-]*:)?PropertyGroup)(?:\s[^>]*)?>", RegexOptions.Singleline)]
    private static partial Regex PropertyGroupPattern();

    /// <summary>数字段相等即视为同一个版本（2.1.2 与 2.1.2.0 算相等）。</summary>
    private static bool NumericallyEqual(string? left, string? right)
    {
        var a = Fields(left);
        var b = Fields(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        for (var i = 0; i < 4; i++)
        {
            var l = i < a.Count ? a[i] : 0;
            var r = i < b.Count ? b[i] : 0;
            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> Fields(string? version)
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

    private static Regex VersionElements() => VersionElementsPattern();

    private static Regex VersionElement() => VersionElementPattern();

    private static Regex VersionSelfClosing() => VersionSelfClosingPattern();

    private static Regex PropertyGroup() => PropertyGroupPattern();
}
