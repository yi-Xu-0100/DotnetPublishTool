namespace DotnetPublishTool.Core.Models;

/// <summary>发布后生成 MSI 安装包的选项（装到 Program Files，走 Windows 标准安装/卸载）。</summary>
public sealed class MsiOptions
{
    public bool Enabled { get; set; }

    /// <summary>产品名（控制面板「程序和功能」与开始菜单里的显示名）；为空时用项目名。</summary>
    public string? ProductName { get; set; }

    /// <summary>发布者/厂商；为空时用产品名。</summary>
    public string? Manufacturer { get; set; }

    /// <summary>安装版本（会被规整成 MSI 要求的 x.y.z）；为空时用项目版本。</summary>
    public string? ProductVersion { get; set; }

    /// <summary>是否创建开始菜单快捷方式。</summary>
    public bool CreateShortcut { get; set; } = true;

    /// <summary>
    /// 安装后在桌面也放一份快捷方式。每机器安装（ALLUSERS=1）时 MSI 的 DesktopFolder 解析成「公共桌面」，
    /// 每用户安装时是当前用户桌面；卸载时一并移除。
    /// </summary>
    public bool CreateDesktopShortcut { get; set; } = true;

    /// <summary>打包 MSI 时自动把版本递增一位（新版本用于本次发布，也可能写回项目）。</summary>
    public bool AutoBumpVersion { get; set; }

    /// <summary>
    /// 自动递增后把新版本写回项目 csproj 的 &lt;Version&gt;。
    /// 这是本工具唯一会改动源文件的地方，默认跟随「自动递增」开启；写前会先把原文件备份到输出目录。
    /// </summary>
    public bool WriteVersionBack { get; set; } = true;

    /// <summary>自动递增前的基准版本；由工作流解析后填入，用于日志与界面提示。</summary>
    public string? BaseVersion { get; set; }

    /// <summary>快捷方式指向的可执行文件（发布目录内的相对路径）；为空时自动识别。</summary>
    public string? EntryExecutable { get; set; }

    /// <summary>升级代码：同一产品的不同版本必须一致；留空时按「厂商 + 产品名」稳定推导。</summary>
    public string? UpgradeCode { get; set; }

    /// <summary>安装包命名方式（复用 zip 的四种口径，扩展名换成 .msi）。</summary>
    public ZipNamingMode NamingMode { get; set; } = ZipNamingMode.ProjectNameVersion;

    /// <summary>日期命名时使用的格式。</summary>
    public string DateFormat { get; set; } = ZipOptions.DefaultDateFormat;

    /// <summary>NamingMode=Custom 时使用。</summary>
    public string? CustomName { get; set; }

    /// <summary>用于「项目名_版本号」的版本号；由工作流在发布前补全。</summary>
    public string? Version { get; set; }

    /// <summary>安装包输出目录；为空时使用默认项目目录。</summary>
    public string? OutputDirectory { get; set; }
}
