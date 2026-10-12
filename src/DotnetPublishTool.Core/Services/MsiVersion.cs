using System.Globalization;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 把任意版本号规整成 MSI 的 ProductVersion 形式（三段；第一、二段 0-255，第三段 0-65535）。
/// </summary>
public static class MsiVersion
{
    public const string Fallback = "1.0.0";

    public static string Normalize(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var values = new List<int>(3);
        foreach (var part in text.Split('.'))
        {
            if (values.Count == 3)
            {
                break;
            }

            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                break;
            }

            values.Add(value);
        }

        if (values.Count == 0)
        {
            return Fallback;
        }

        while (values.Count < 3)
        {
            values.Add(0);
        }

        return Math.Min(values[0], 255) + "." + Math.Min(values[1], 255) + "." + Math.Min(values[2], 65535);
    }
}
