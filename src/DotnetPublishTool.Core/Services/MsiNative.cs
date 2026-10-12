using System.Runtime.InteropServices;

namespace DotnetPublishTool.Core.Services;

/// <summary>
/// Windows Installer 数据库 API（msi.dll）的最小 P/Invoke 包装。
/// 只用系统自带的 msi.dll，不引入任何第三方包或外部工具链。
/// </summary>
internal static class MsiNative
{
    /// <summary>MSIDBOPEN_CREATE：新建数据库（存在同名文件时覆盖）。</summary>
    internal const int OpenCreate = 3;

    /// <summary>VARIANT 类型：VT_I2（摘要信息里的 Codepage 用它）。</summary>
    internal const uint VariantI2 = 2;

    /// <summary>VARIANT 类型：VT_I4。</summary>
    internal const uint VariantI4 = 3;

    /// <summary>VARIANT 类型：VT_LPSTR / VT_BSTR 字符串。</summary>
    internal const uint VariantString = 30;

    // 摘要信息（Summary Information）里用到的属性号。
    /// <summary>摘要信息字符串所用的 ANSI 代码页；必须在写任何字符串之前设置。</summary>
    internal const uint SummaryCodepage = 1;

    internal const uint SummaryTitle = 2;
    internal const uint SummarySubject = 3;
    internal const uint SummaryAuthor = 4;
    internal const uint SummaryKeywords = 5;
    internal const uint SummaryComments = 6;
    internal const uint SummaryTemplate = 7;
    internal const uint SummaryRevisionNumber = 9;
    internal const uint SummaryPageCount = 14;
    internal const uint SummaryWordCount = 15;
    internal const uint SummaryCreatingApplication = 18;
    internal const uint SummarySecurity = 19;

    /// <summary>当前系统的 ANSI 代码页（摘要信息里的字符串按它转 ANSI 存储）。</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetACP();

    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint OpenDatabase(string databasePath, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiCloseHandle(IntPtr handle);

    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint DatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    /// <summary>取不到下一行时的返回码（读表循环靠它结束）。</summary>
    internal const uint ErrorNoMoreItems = 259;

    /// <summary>字符串缓冲区不够大时的返回码（先问长度用）。</summary>
    internal const uint ErrorMoreData = 234;

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiRecordGetFieldCount(IntPtr record);

    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RecordGetString(IntPtr record, uint field, System.Text.StringBuilder value, ref uint size);

    /// <summary>注意：MsiCreateRecord 直接返回记录句柄（失败时为 0），不是错误码 + out 参数。</summary>
    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern IntPtr MsiCreateRecord(uint fieldCount);

    [DllImport("msi.dll", EntryPoint = "MsiRecordSetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RecordSetString(IntPtr record, uint field, string value);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiRecordSetInteger(IntPtr record, uint field, int value);

    [DllImport("msi.dll", EntryPoint = "MsiRecordSetStreamW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RecordSetStream(IntPtr record, uint field, string filePath);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiDatabaseCommit(IntPtr database);

    [DllImport("msi.dll", EntryPoint = "MsiGetSummaryInformationW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint GetSummaryInformation(IntPtr database, string? databasePath, uint updateCount, out IntPtr summary);

    [DllImport("msi.dll", EntryPoint = "MsiSummaryInfoSetPropertyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint SummaryInfoSetProperty(IntPtr summary, uint property, uint dataType, int intValue, IntPtr fileTime, string? stringValue);

    [DllImport("msi.dll", ExactSpelling = true)]
    internal static extern uint MsiSummaryInfoPersist(IntPtr summary);
}
