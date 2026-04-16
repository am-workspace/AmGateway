using AmGateway.Abstractions.Models;

namespace AmGateway.Abstractions;

/// <summary>
/// 北向发布器接口 - 所有北向输出插件必须实现
/// 与南向 IProtocolDriver 对称设计
/// </summary>
public interface IPublisher : IAsyncDisposable
{
    /// <summary>
    /// 发布器唯一标识
    /// </summary>
    string PublisherId { get; }

    /// <summary>
    /// 初始化发布器，注入上下文（配置、日志）
    /// </summary>
    Task InitializeAsync(PublisherContext context, CancellationToken ct = default);

    /// <summary>
    /// 启动发布器（连接外部系统等）
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止发布器（优雅关闭）
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 发布单个数据点
    /// </summary>
    ValueTask PublishAsync(DataPoint point, CancellationToken ct = default);

    /// <summary>
    /// 批量发布数据点
    /// </summary>
    ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default);
}
