using System.ComponentModel;
using System.Text;
using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// 正在写入的一个 MSI 数据库：建表、插行、插流、写摘要信息、提交。
/// 出错时抛出的消息带上 Windows Installer 错误码与系统说明，方便定位是哪条语句出的问题。
/// </summary>
internal sealed class MsiDatabase : IDisposable
{
    private IntPtr _handle;

    public MsiDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Check(MsiNative.OpenDatabase(path, (IntPtr)MsiNative.OpenCreate, out _handle), "创建 MSI 数据库 " + path);
    }

    public void Execute(string sql)
    {
        var view = OpenView(sql);
        try
        {
            Check(MsiNative.MsiViewExecute(view, IntPtr.Zero), sql);
        }
        finally
        {
            MsiNative.MsiCloseHandle(view);
        }
    }

    /// <summary>表是否存在。用「能不能打开一个视图」判断，不做任何写入。</summary>
    public bool TableExists(string table)
    {
        if (MsiNative.DatabaseOpenView(_handle, "SELECT * FROM `" + table + "`", out var view) != 0)
        {
            return false;
        }

        MsiNative.MsiCloseHandle(view);
        return true;
    }

    public void Insert(string sql, params object?[] values)
    {
        var view = OpenView(sql);
        var record = IntPtr.Zero;
        try
        {
            if (values.Length > 0)
            {
                record = MsiNative.MsiCreateRecord((uint)values.Length);
                if (record == IntPtr.Zero)
                {
                    throw new PublishException("生成 MSI 失败：无法创建数据记录（" + sql + "）。");
                }
            }

            for (var index = 0; index < values.Length; index++)
            {
                var field = (uint)(index + 1);
                switch (values[index])
                {
                    case null:
                        break;
                    case int number:
                        Check(MsiNative.MsiRecordSetInteger(record, field, number), $"写第 {field} 个字段为整数（{sql}）");
                        break;
                    case long number:
                        Check(MsiNative.MsiRecordSetInteger(record, field, (int)number), $"写第 {field} 个字段为整数（{sql}）");
                        break;
                    default:
                        Check(MsiNative.RecordSetString(record, field, (string)values[index]!), $"写第 {field} 个字段为文本（{sql}）");
                        break;
                }
            }

            Check(MsiNative.MsiViewExecute(view, record), "执行语句（" + sql + "）");
        }
        finally
        {
            if (record != IntPtr.Zero)
            {
                MsiNative.MsiCloseHandle(record);
            }

            MsiNative.MsiCloseHandle(view);
        }
    }

    /// <summary>读表（打完包自检用）：返回每一行的字段值（一律按字符串取，NULL 得空串）。</summary>
    public IReadOnlyList<string[]> Query(string sql)
    {
        var rows = new List<string[]>();
        var view = OpenView(sql);
        try
        {
            Check(MsiNative.MsiViewExecute(view, IntPtr.Zero), sql);
            while (true)
            {
                var status = MsiNative.MsiViewFetch(view, out var record);
                if (status == MsiNative.ErrorNoMoreItems)
                {
                    break;
                }

                Check(status, sql);
                try
                {
                    var count = (int)MsiNative.MsiRecordGetFieldCount(record);
                    var values = new string[count];
                    for (var index = 0; index < count; index++)
                    {
                        values[index] = ReadString(record, (uint)(index + 1));
                    }

                    rows.Add(values);
                }
                finally
                {
                    MsiNative.MsiCloseHandle(record);
                }
            }
        }
        finally
        {
            MsiNative.MsiCloseHandle(view);
        }

        return rows;
    }

    private static string ReadString(IntPtr record, uint field)
    {
        var buffer = new StringBuilder(256);
        var size = (uint)buffer.Capacity;
        var status = MsiNative.RecordGetString(record, field, buffer, ref size);
        if (status == MsiNative.ErrorMoreData)
        {
            buffer = new StringBuilder((int)size + 1);
            size = (uint)buffer.Capacity;
            MsiNative.RecordGetString(record, field, buffer, ref size);
        }

        return buffer.ToString();
    }

    /// <summary>把 cab 作为流写进 _Streams，Media 表里用 #流名 引用它，安装包因此只有一个文件。</summary>
    public void InsertStream(string name, string filePath)
    {
        const string sql = "INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)";
        var view = OpenView(sql);
        var record = IntPtr.Zero;
        try
        {
            record = MsiNative.MsiCreateRecord(2);
            if (record == IntPtr.Zero)
            {
                throw new PublishException("生成 MSI 失败：无法创建数据记录（" + sql + "）。");
            }

            Check(MsiNative.RecordSetString(record, 1, name), sql);
            Check(MsiNative.RecordSetStream(record, 2, filePath), "读取 cab 文件 " + filePath);
            Check(MsiNative.MsiViewExecute(view, record), sql);
        }
        finally
        {
            if (record != IntPtr.Zero)
            {
                MsiNative.MsiCloseHandle(record);
            }

            MsiNative.MsiCloseHandle(view);
        }
    }

    /// <summary>写摘要信息：没有它 msiexec 会拒绝这个安装包。</summary>
    public void WriteSummaryInformation(string productName, string manufacturer, string template, string packageCode)
    {
        Check(MsiNative.GetSummaryInformation(_handle, null, 20, out var summary), "打开摘要信息");
        try
        {
            // 顺序有讲究：摘要信息里的字符串按 Codepage 转成 ANSI 存储，官方要求先设 Codepage 再写字符串；
            // 缺了它，引擎打开安装包会直接返回 87（参数错误）。
            SetSummaryShort(summary, MsiNative.SummaryCodepage, AnsiCodePage);
            SetSummaryString(summary, MsiNative.SummaryTitle, productName);
            SetSummaryString(summary, MsiNative.SummarySubject, productName);
            SetSummaryString(summary, MsiNative.SummaryAuthor, manufacturer);
            SetSummaryString(summary, MsiNative.SummaryKeywords, "Installer, MSI, Database");
            SetSummaryString(summary, MsiNative.SummaryComments, "该安装包由 DotnetPublishTool 生成。");
            SetSummaryString(summary, MsiNative.SummaryTemplate, template);
            SetSummaryString(summary, MsiNative.SummaryRevisionNumber, packageCode);
            SetSummaryString(summary, MsiNative.SummaryCreatingApplication, "DotnetPublishTool");
            // Page Count = 需要的最低安装器版本（200 = Windows Installer 2.0；64 位包要求 >= 200）。
            SetSummaryInteger(summary, MsiNative.SummaryPageCount, 200);
            // Word Count：bit1=2 表示源是压缩的；bit3=8 表示「安装不需要提升权限」。
            // 这里是每机器安装（装到 Program Files），所以不加 bit3。
            SetSummaryInteger(summary, MsiNative.SummaryWordCount, 2);
            // Security：安装数据库建议标成「推荐只读」。
            SetSummaryInteger(summary, MsiNative.SummarySecurity, 2);
            Check(MsiNative.MsiSummaryInfoPersist(summary), "写入摘要信息");
        }
        finally
        {
            MsiNative.MsiCloseHandle(summary);
        }
    }

    public void Commit() => Check(MsiNative.MsiDatabaseCommit(_handle), "提交 MSI 数据库");

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            MsiNative.MsiCloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private IntPtr OpenView(string sql) =>
        MsiNative.DatabaseOpenView(_handle, sql, out var view) == 0
            ? view
            : throw new PublishException($"生成 MSI 失败：无法解析语句 {sql}。");

    private static void SetSummaryString(IntPtr summary, uint property, string value) =>
        Check(MsiNative.SummaryInfoSetProperty(summary, property, MsiNative.VariantString, 0, IntPtr.Zero, value), "写摘要信息");

    private static void SetSummaryInteger(IntPtr summary, uint property, int value) =>
        Check(MsiNative.SummaryInfoSetProperty(summary, property, MsiNative.VariantI4, value, IntPtr.Zero, null), "写摘要信息");

    private static void SetSummaryShort(IntPtr summary, uint property, int value) =>
        Check(MsiNative.SummaryInfoSetProperty(summary, property, MsiNative.VariantI2, value, IntPtr.Zero, null), "写摘要信息");

    /// <summary>系统 ANSI 代码页；取不到时退回 1252。</summary>
    private static int AnsiCodePage
    {
        get
        {
            try
            {
                var codePage = (int)MsiNative.GetACP();
                return codePage > 0 ? codePage : 1252;
            }
            catch
            {
                return 1252;
            }
        }
    }

    private static void Check(uint code, string operation)
    {
        if (code == 0)
        {
            return;
        }

        throw new PublishException($"生成 MSI 失败：{operation} 返回 Windows Installer 错误 {code}（{Describe(code)}）。");
    }

    private static string Describe(uint code)
    {
        switch (code)
        {
            case 1101: return "无法打开数据库文件";
            case 1620: return "安装包无法打开";
            case 1627: return "执行失败";
            case 1630: return "数据类型不受支持（表结构或列类型不匹配）";
            case 1631: return "提供程序失败";
            case 1632: return "临时文件夹不可用";
            case 1633: return "临时文件夹里的文件无法访问";
            case 1635: return "无法打开安装包";
            case 1638: return "已存在同名产品的另一个版本";
        }

        try
        {
            return new Win32Exception((int)code).Message;
        }
        catch
        {
            return "未知错误";
        }
    }
}
