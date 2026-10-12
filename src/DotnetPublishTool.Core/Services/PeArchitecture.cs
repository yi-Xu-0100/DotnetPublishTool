namespace DotnetPublishTool.Core.Services;

/// <summary>只读 PE 头，判断一个可执行文件是 32 位还是 64 位。</summary>
internal static class PeArchitecture
{
    public static bool? Is64Bit(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> dosHeader = stackalloc byte[64];
            if (stream.Read(dosHeader) < 64 || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                return null;
            }

            var peOffset = BitConverter.ToInt32(dosHeader[0x3C..0x40]);
            if (peOffset <= 0)
            {
                return null;
            }

            stream.Position = peOffset;
            Span<byte> peHeader = stackalloc byte[6];
            if (stream.Read(peHeader) < 6 || peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E')
            {
                return null;
            }

            var machine = BitConverter.ToUInt16(peHeader[4..6]);
            return machine switch
            {
                0x8664 => true,
                0x14C => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
