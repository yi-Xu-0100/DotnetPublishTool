namespace DotnetPublishTool.Core.Models;

/// <summary>发布流程中可预期的失败；Message 直接面向用户，可安全显示在对话框里。</summary>
public sealed class PublishException : Exception
{
    public PublishException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
