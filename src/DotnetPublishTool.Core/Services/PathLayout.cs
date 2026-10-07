namespace DotnetPublishTool.Core.Services;

/// <summary>默认输出目录约定。</summary>
/// <remarks>
/// 默认布局：&lt;文档&gt;\DotnetPublishTool\&lt;项目名&gt;\{publish,profiles}。
/// </remarks>
public static class PathLayout
{
    /// <summary>文档下的默认子目录名。</summary>
    public const string DefaultOutputFolderName = "DotnetPublishTool";

    /// <summary>配置副本目录名。</summary>
    public const string ProfilesFolderName = "profiles";

    /// <summary>发布输出目录名。</summary>
    public const string PublishFolderName = "publish";

    /// <summary>默认输出根目录：文档/DotnetPublishTool。</summary>
    public static string DefaultOutputRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        DefaultOutputFolderName);

    /// <summary>某个项目的输出目录：&lt;根&gt;\&lt;项目名&gt;。</summary>
    public static string GetProjectRoot(string outputRoot, string projectName) =>
        Path.Combine(outputRoot, Sanitize(projectName));

    /// <summary>发布目录：&lt;根&gt;\&lt;项目名&gt;\publish。</summary>
    public static string GetPublishDirectory(string outputRoot, string projectName) =>
        Path.Combine(GetProjectRoot(outputRoot, projectName), PublishFolderName);

    /// <summary>配置副本目录：&lt;根&gt;\&lt;项目名&gt;\profiles。</summary>
    public static string GetProfileDirectory(string outputRoot, string projectName) =>
        Path.Combine(GetProjectRoot(outputRoot, projectName), ProfilesFolderName);

    /// <summary>把任意文本规整为合法的文件/目录名。</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "project";
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            buffer[i] = invalid.Contains(name[i]) ? '_' : name[i];
        }

        var result = new string(buffer).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "project" : result;
    }
}
