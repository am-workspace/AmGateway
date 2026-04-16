using AmGateway.Abstractions.Models;

namespace AmGateway.Abstractions;

/// <summary>
/// 数据输出接口 - 驱动通过此接口将采集到的 DataPoint 推送到数据管道
/// </summary>
public interface IDataSink
{
    /// <summary>
    /// 发布单个数据点
    /// </summary>
    ValueTask PublishAsync(DataPoint point, CancellationToken ct = default);

    /// <summary>
    /// 批量发布数据点
    /// </summary>
    ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default);
}
