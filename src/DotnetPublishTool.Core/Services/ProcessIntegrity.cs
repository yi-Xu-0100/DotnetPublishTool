using System.Runtime.InteropServices;

namespace DotnetPublishTool.Core.Services;

/// <summary>Windows 强制完整性级别（Mandatory Integrity Control）。</summary>
public enum ProcessIntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    System = 5,
}

/// <summary>
/// 查询当前进程的完整性级别。
/// </summary>
/// <remarks>
/// 这是排查「把产物放到文档目录却被拒绝」的关键：带 Low 标签的 exe 启动后进程就是 Low 完整性，
/// 而 Low 进程只能写 Low 对象，写「文档」「%APPDATA%」这类 Medium 对象一律被拒 —— 与 ACL、与从哪里启动都无关。
/// 注意 WindowsIdentity.Groups 不包含完整性 SID（实测），只能查令牌。
/// </remarks>
public static class ProcessIntegrity
{
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQuery = 0x0008;

    private const uint RidUntrusted = 0x0000;
    private const uint RidLow = 0x1000;
    private const uint RidMedium = 0x2000;
    private const uint RidHigh = 0x3000;
    private const uint RidSystem = 0x4000;

    public static ProcessIntegrityLevel GetCurrent() => GetForCurrentProcess();

    /// <summary>是否低于 Medium —— 这类进程写不了「文档」「%APPDATA%」等常规用户目录。</summary>
    public static bool IsBelowMedium => GetCurrent() is ProcessIntegrityLevel.Untrusted or ProcessIntegrityLevel.Low;

    public static string Describe(ProcessIntegrityLevel level) => level switch
    {
        ProcessIntegrityLevel.Untrusted => "Untrusted（不可信）",
        ProcessIntegrityLevel.Low => "Low（低）",
        ProcessIntegrityLevel.Medium => "Medium（中）",
        ProcessIntegrityLevel.High => "High（高）",
        ProcessIntegrityLevel.System => "System（系统）",
        _ => "未知",
    };

    private static ProcessIntegrityLevel GetForCurrentProcess()
    {
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return ProcessIntegrityLevel.Unknown;
            }

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0)
            {
                return ProcessIntegrityLevel.Unknown;
            }

            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
            {
                return ProcessIntegrityLevel.Unknown;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label }，Sid 位于偏移 0。
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return ProcessIntegrityLevel.Unknown;
            }

            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero)
            {
                return ProcessIntegrityLevel.Unknown;
            }

            var subAuthorityCount = Marshal.ReadByte(countPointer);
            if (subAuthorityCount == 0)
            {
                return ProcessIntegrityLevel.Unknown;
            }

            var ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
            if (ridPointer == IntPtr.Zero)
            {
                return ProcessIntegrityLevel.Unknown;
            }

            return (uint)Marshal.ReadInt32(ridPointer) switch
            {
                RidUntrusted => ProcessIntegrityLevel.Untrusted,
                RidLow => ProcessIntegrityLevel.Low,
                RidMedium => ProcessIntegrityLevel.Medium,
                RidHigh => ProcessIntegrityLevel.High,
                RidSystem => ProcessIntegrityLevel.System,
                _ => ProcessIntegrityLevel.Unknown,
            };
        }
        catch
        {
            return ProcessIntegrityLevel.Unknown;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
