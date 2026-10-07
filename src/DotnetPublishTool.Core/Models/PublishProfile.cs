namespace DotnetPublishTool.Core.Models;

/// <summary>发布配置中的一条 MSBuild 属性。</summary>
public sealed class ProfileProperty
{
    public ProfileProperty()
    {
    }

    public ProfileProperty(string key, string? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>属性名，例如 Configuration、RuntimeIdentifier。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>属性值。</summary>
    public string? Value { get; set; }

    public ProfileProperty Clone() => new(Key, Value);
}

/// <summary>一个发布配置（.pubxml）。Properties 为项目文件之外的可编辑载体，发布时写到输出目录。</summary>
public sealed class PublishProfile
{
    /// <summary>配置名（.pubxml 文件名，不含扩展名）。</summary>
    public required string Name { get; init; }

    /// <summary>原始 .pubxml 路径；新建的默认配置为 null。</summary>
    public string? SourcePath { get; init; }

    /// <summary>是否为工具生成的默认配置。</summary>
    public bool IsSynthetic { get; init; }

    /// <summary>配置中的属性（已摊平，同名后者覆盖前者）。</summary>
    public List<ProfileProperty> Properties { get; init; } = [];

    /// <summary>原配置里非 PropertyGroup 的片段（例如 ItemGroup），保存时原样保留。</summary>
    public string? ItemGroupsXml { get; init; }

    public string? GetValue(string key) =>
        Properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    public bool? GetBool(string key)
    {
        var value = GetValue(key);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return null;
    }

    public string DisplayName => IsSynthetic ? $"{Name}（工具默认）" : Name;

    public string SourceText => SourcePath ?? "工具内置默认配置";
}
