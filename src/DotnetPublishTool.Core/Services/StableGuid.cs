using System.Security.Cryptography;
using System.Text;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 由文本稳定推导 GUID。同一个「厂商 + 产品名」或「升级代码 + 相对路径」每次都得到同一个值，
/// 因此无需把状态写进配置，后续版本也能识别并升级已安装的产品。
/// </summary>
internal static class StableGuid
{
    public static Guid From(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("DotnetPublishTool.Msi|" + seed));
        var bytes = hash[..16];

        // 只为了得到一个形状正常的 GUID（版本/变体位），不参与安全用途。
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>MSI 表里使用的 {XXXXXXXX-XXXX-...} 形式（大写）。</summary>
    public static string ToMsiString(Guid guid) => "{" + guid.ToString("D").ToUpperInvariant() + "}";

    /// <summary>把用户填写的升级代码规整为 {GUID}；无法解析时抛出可读异常。</summary>
    public static string Normalize(string value, string fieldName)
    {
        var text = value.Trim().Trim('{', '}');
        if (Guid.TryParse(text, out var guid))
        {
            return ToMsiString(guid);
        }

        throw new Models.PublishException($"{fieldName}不是合法的 GUID：{value}");
    }
}
