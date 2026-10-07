using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 读写界面设置。默认位置是 %APPDATA%\DotnetPublishTool\settings.json；
/// 该位置不可写时（受限账户、沙箱/低完整性进程等）自动退到 %TEMP%\DotnetPublishTool\settings.json，
/// 免得「最近项目」这类记录悄悄存不下来。
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _primaryPath;
    private string? _effectivePath;

    public AppSettingsService(string? settingsPath = null)
    {
        _primaryPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DotnetPublishTool",
            "settings.json");
    }

    /// <summary>实际使用的设置文件路径（一旦成功读写就固定下来）。</summary>
    public string SettingsPath => _effectivePath ?? _primaryPath;

    public AppSettings Load()
    {
        foreach (var path in GetCandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions)
                    ?? new AppSettings();
                _effectivePath = path;
                return Migrate(settings);
            }
            catch
            {
                // 该候选不可用，继续下一个。
            }
        }

        return new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, SerializerOptions);

        foreach (var path in GetCandidatePaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, json);
                _effectivePath = path;
                return;
            }
            catch
            {
                // 该候选不可写，继续下一个。
            }
        }
    }

    private IEnumerable<string> GetCandidatePaths()
    {
        if (!string.IsNullOrWhiteSpace(_effectivePath))
        {
            yield return _effectivePath!;
        }

        yield return _primaryPath;

        var temp = Path.Combine(Path.GetTempPath(), "DotnetPublishTool", "settings.json");
        if (!string.Equals(temp, _primaryPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return temp;
        }
    }

    /// <summary>补齐新增字段，并按版本逐步升级旧设置的默认值。</summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        settings.RecentProjects ??= [];

        // v2：默认命名改为「项目名_日期」，日期格式带时分秒。
        if (settings.SettingsVersion < 2)
        {
            settings.ZipNamingMode = ZipNamingMode.ProjectNameDate;
            settings.ZipDateFormat = ZipOptions.DefaultDateFormat;
        }

        // v3：压缩包默认不再包含顶层文件夹。
        if (settings.SettingsVersion < 3)
        {
            settings.ZipIncludeRootFolder = false;
        }

        settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
        return settings;
    }
}
