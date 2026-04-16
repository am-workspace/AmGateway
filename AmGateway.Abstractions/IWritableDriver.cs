using AmGateway.Abstractions.Models;

namespace AmGateway.Abstractions;

/// <summary>
/// 可写驱动接口（预定义，Phase 1 不实现）
/// 支持写操作的驱动同时实现 IProtocolDriver 和 IWritableDriver
/// 主机侧通过 if (driver is IWritableDriver writable) 检测写能力
/// </summary>
public interface IWritableDriver
{
    /// <summary>
    /// 向设备写入数据
    /// </summary>
    Task<WriteResult> WriteAsync(WriteCommand command, CancellationToken ct = default);
}
