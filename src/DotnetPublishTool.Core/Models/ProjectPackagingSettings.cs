namespace DotnetPublishTool.Core.Models;

/// <summary>单个项目的 ZIP / MSI 持久化选项。</summary>
public sealed class ProjectPackagingSettings
{
    public bool MakeZip { get; set; } = true;
    public ZipNamingMode ZipNamingMode { get; set; } = ZipNamingMode.ProjectNameDate;
    public string ZipDateFormat { get; set; } = ZipOptions.DefaultDateFormat;
    public string? CustomZipName { get; set; }
    public bool ZipIncludeRootFolder { get; set; }

    public bool MakeMsi { get; set; }
    public string? MsiProductName { get; set; }
    public string? MsiManufacturer { get; set; }
    public bool MsiCreateShortcut { get; set; } = true;
    public bool MsiCreateDesktopShortcut { get; set; } = true;
    public ZipNamingMode MsiNamingMode { get; set; } = ZipNamingMode.ProjectNameVersion;
    public string? MsiCustomName { get; set; }
    public bool MsiAutoBumpVersion { get; set; }
    public bool MsiWriteVersionBack { get; set; } = true;
}
