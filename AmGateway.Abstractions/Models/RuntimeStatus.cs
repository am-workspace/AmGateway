namespace AmGateway.Abstractions.Models;

/// <summary>
/// 运行时实例状态
/// </summary>
public enum RuntimeStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}
